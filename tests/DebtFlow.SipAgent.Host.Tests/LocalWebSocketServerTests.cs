using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class LocalWebSocketServerTests
{
    [Fact]
    public async Task DegradedConfiguration_RejectsBeforeWebSocketUpgrade()
    {
        var endpoint = new LocalWebSocketServer(
            null!,
            null!,
            null!,
            null!,
            NullLogger<LocalWebSocketServer>.Instance,
            new AgentRuntimeOptions(
                ConsoleMode: false,
                BackgroundMode: true,
                IsAllowAllOrigins: false,
                new HashSet<string>(["https://portal.example.test"], StringComparer.Ordinal),
                ConfigurationError: "origin_configuration_invalid"));
        var context = new DefaultHttpContext();

        await endpoint.HandleAsync(context);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task SecurityAndSessionContract_RejectsUnsafeRequestsAndEnforcesSingleOwnerAndRateLimit()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await store.InitializeAsync(CancellationToken.None);
        await using var storeLifetime = store;
        var publisher = new WebSocketEventPublisher();
        await using var publisherLifetime = publisher;
        var runtime = new FakeSipRuntime();
        await using var coordinator = new AgentCoordinator(
            runtime,
            store,
            publisher,
            new SystemAgentClock(),
            new GuidAgentIdGenerator());
        var dispatcher = new V1CommandDispatcher(coordinator, store, new SystemAgentClock());
        var options = new AgentRuntimeOptions(
            ConsoleMode: true,
            BackgroundMode: false,
            IsAllowAllOrigins: false,
            new HashSet<string>(["http://localhost:8765"], StringComparer.Ordinal),
            ConfigurationError: null);
        var endpoint = new LocalWebSocketServer(
            coordinator,
            store,
            publisher,
            dispatcher,
            NullLogger<LocalWebSocketServer>.Instance,
            options);

        using X509Certificate2 testCertificate = CreateTestCertificate();
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(testCertificate)));
        await using WebApplication app = builder.Build();
        app.UseWebSockets();
        app.Map("/agent/v1", endpoint.HandleAsync);
        await app.StartAsync();

        try
        {
            string httpsAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var httpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new HttpClient(httpHandler);
            using HttpResponseMessage queryRejected = await http.GetAsync(
                httpsAddress + "/agent/v1?password=must-not-be-accepted");
            Assert.Equal(HttpStatusCode.BadRequest, queryRejected.StatusCode);

            using var wrongOrigin = new HttpRequestMessage(HttpMethod.Get, httpsAddress + "/agent/v1")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            wrongOrigin.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            wrongOrigin.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            wrongOrigin.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            wrongOrigin.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
            wrongOrigin.Headers.TryAddWithoutValidation("Origin", "https://untrusted.example.test");
            using HttpResponseMessage originRejected = await http.SendAsync(wrongOrigin);
            Assert.Equal(HttpStatusCode.Forbidden, originRejected.StatusCode);

            var uri = new Uri(httpsAddress.Replace("https://", "wss://", StringComparison.Ordinal) + "/agent/v1");
            using (var invalidHello = CreateClient("http://localhost:8765"))
            {
                await invalidHello.ConnectAsync(uri, CancellationToken.None);
                string invalidMessageId = ProtocolCodec.NewId();
                await SendAsync(
                    invalidHello,
                    ProtocolCodec.Serialize(
                        "hello",
                        "session.hello",
                        new HelloPayload("test", []),
                        invalidMessageId),
                    CancellationToken.None);
                ProtocolEnvelope invalidError = ProtocolCodec.Deserialize(
                    await ReceiveAsync(invalidHello, CancellationToken.None));
                ErrorPayload invalidPayload = ProtocolCodec.DeserializePayload<ErrorPayload>(invalidError.Payload);
                Assert.Equal(invalidMessageId, invalidPayload.CorrelationMessageId);
                Assert.Equal("invalid_message", invalidPayload.Code);
            }

            await WaitUntilAsync(() => !endpoint.IsClientConnected);
            using var first = CreateClient("http://localhost:8765");
            await first.ConnectAsync(uri, CancellationToken.None);
            await SendAsync(
                first,
                ProtocolCodec.Serialize(
                    "hello",
                    "session.hello",
                    new HelloPayload("test", [ProtocolConstants.Version])),
                CancellationToken.None);

            ProtocolEnvelope welcome = ProtocolCodec.Deserialize(await ReceiveAsync(first, CancellationToken.None));
            ProtocolEnvelope snapshot = ProtocolCodec.Deserialize(await ReceiveAsync(first, CancellationToken.None));
            Assert.Equal("welcome", welcome.Kind);
            Assert.Equal("session.welcome", welcome.Type);
            Assert.Equal(
                "1.0.0",
                ProtocolCodec.DeserializePayload<WelcomePayload>(welcome.Payload).AgentVersion);
            Assert.Contains(
                "event.collection_binding.v1",
                ProtocolCodec.DeserializePayload<WelcomePayload>(welcome.Payload).Capabilities);
            Assert.Equal("snapshot", snapshot.Kind);
            Assert.Equal("agent.snapshot", snapshot.Type);

            using var second = CreateClient("http://localhost:8765");
            await second.ConnectAsync(uri, CancellationToken.None);
            ProtocolEnvelope busy = ProtocolCodec.Deserialize(await ReceiveAsync(second, CancellationToken.None));
            ErrorPayload payload = ProtocolCodec.DeserializePayload<ErrorPayload>(busy.Payload);
            Assert.Equal("error", busy.Kind);
            Assert.Equal("client_already_connected", payload.Code);

            for (int index = 0; index < ProtocolConstants.MaximumMessagesPerWindow - 1; index++)
            {
                await SendAsync(
                    first,
                    ProtocolCodec.Serialize("ping", "session.ping", new { }),
                    CancellationToken.None);
                ProtocolEnvelope pong = ProtocolCodec.Deserialize(await ReceiveAsync(first, CancellationToken.None));
                Assert.Equal("pong", pong.Kind);
            }

            await SendAsync(
                first,
                ProtocolCodec.Serialize("ping", "session.ping", new { }),
                CancellationToken.None);
            ProtocolEnvelope limited = ProtocolCodec.Deserialize(await ReceiveAsync(first, CancellationToken.None));
            Assert.Equal(
                "rate_limit_exceeded",
                ProtocolCodec.DeserializePayload<ErrorPayload>(limited.Payload).Code);
            first.Abort();

            await WaitUntilAsync(() => !endpoint.IsClientConnected);
            using (var oversized = CreateClient("http://localhost:8765"))
            {
                await oversized.ConnectAsync(uri, CancellationToken.None);
                byte[] tooLarge = Enumerable.Repeat((byte)'x', ProtocolConstants.MaximumMessageBytes + 1).ToArray();
                await SendAsync(oversized, tooLarge, CancellationToken.None);
                ProtocolEnvelope sizeError = ProtocolCodec.Deserialize(
                    await ReceiveAsync(oversized, CancellationToken.None));
                ErrorPayload sizePayload = ProtocolCodec.DeserializePayload<ErrorPayload>(sizeError.Payload);
                Assert.Null(sizePayload.CorrelationMessageId);
                Assert.Equal("message_too_large", sizePayload.Code);
            }
        }
        finally
        {
            await app.StopAsync();
            await coordinator.DisposeAsync();
            await publisher.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ClientWebSocket CreateClient(string origin)
    {
        var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", origin);
        client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return client;
    }

    private static X509Certificate2 CreateTestCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }

    private static Task SendAsync(ClientWebSocket socket, byte[] message, CancellationToken cancellationToken) =>
        socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var result = new List<byte>();
        byte[] chunk = new byte[4096];
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(chunk, cancellationToken);
            result.AddRange(chunk.AsSpan(0, received.Count).ToArray());
        }
        while (!received.EndOfMessage);

        return result.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class FakeSipRuntime : ISipRuntime
    {
        public event Func<SipSignal, Task>? Signal
        {
            add { }
            remove { }
        }
        public string AudioState => "ready";
        public Task ConfigureAsync(SipConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartRegistrationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopRegistrationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartCallAsync(string destination, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AnswerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RejectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RejectUnavailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HangupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
