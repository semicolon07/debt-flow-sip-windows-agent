using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class LocalWebSocketServer(
    AgentCoordinator coordinator,
    IAgentEventStore eventStore,
    WebSocketEventPublisher publisher,
    V1CommandDispatcher dispatcher,
    ILogger<LocalWebSocketServer> logger,
    IReadOnlySet<string> allowedOrigins) : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private int _clientConnected;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Prefixes.Add("http://localhost:8443/");
        _listener.Start();
        logger.LogInformation("SIP agent listening on ws://localhost:8443/agent/v1");

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                _ = ProcessContextAsync(context, cancellationToken);
            }
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Close();
        return ValueTask.CompletedTask;
    }

    private async Task ProcessContextAsync(HttpListenerContext context, CancellationToken applicationToken)
    {
        if (!string.Equals(context.Request.Url?.AbsolutePath, "/agent/v1", StringComparison.Ordinal) ||
            !context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
            return;
        }

        string? origin = context.Request.Headers["Origin"];
        if (!OriginPolicy.IsAllowed(origin, allowedOrigins))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            context.Response.Close();
            return;
        }

        if (Interlocked.CompareExchange(ref _clientConnected, 1, 0) != 0)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Conflict;
            context.Response.Close();
            return;
        }

        WebSocket? socket = null;
        try
        {
            HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
            socket = webSocketContext.WebSocket;
            if (!publisher.TryAttach(socket))
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "client_already_connected", applicationToken);
                return;
            }

            ProtocolEnvelope hello = await ReceiveAsync(socket, applicationToken);
            ValidateHello(hello);
            HelloPayload helloPayload = ProtocolCodec.DeserializePayload<HelloPayload>(hello.Payload);
            if (!helloPayload.SupportedProtocolVersions.Contains(ProtocolConstants.Version))
            {
                await publisher.SendControlAsync(
                    "error",
                    "protocol.error",
                    new ErrorPayload(hello.MessageId, "protocol_version_unsupported", false),
                    applicationToken);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "protocol_version_unsupported", applicationToken);
                return;
            }

            await SendWelcomeAndStateAsync(applicationToken);
            await ReplayPendingEventsAsync(applicationToken);

            while (socket.State == WebSocketState.Open && !applicationToken.IsCancellationRequested)
            {
                ProtocolEnvelope message = await ReceiveAsync(socket, applicationToken);
                if (message.Kind == "ping" && message.Type == "session.ping")
                {
                    await publisher.SendControlAsync(
                        "pong",
                        "session.pong",
                        new { correlationMessageId = message.MessageId },
                        applicationToken);
                    continue;
                }

                byte[] response = await dispatcher.DispatchAsync(message, applicationToken);
                await publisher.SendRawAsync(response, applicationToken);
                if (message.Kind == "command" && message.Type == "state.get")
                {
                    await SendSnapshotAsync(applicationToken);
                }
            }
        }
        catch (ProtocolException exception)
        {
            logger.LogWarning("WebSocket protocol message rejected with code {Code}", exception.Code);
            if (socket is { State: WebSocketState.Open })
            {
                await publisher.SendControlAsync(
                    "error",
                    "protocol.error",
                    new ErrorPayload(null, exception.Code, false),
                    CancellationToken.None);
                await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, exception.Code, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            if (socket is { State: WebSocketState.Open })
            {
                await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "timeout", CancellationToken.None);
            }
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation("WebSocket disconnected with {ErrorType}", exception.GetType().Name);
        }
        catch (Exception exception)
        {
            logger.LogError("WebSocket session failed with {ErrorType}", exception.GetType().Name);
        }
        finally
        {
            if (socket != null)
            {
                publisher.Detach(socket);
                socket.Dispose();
            }

            Interlocked.Exchange(ref _clientConnected, 0);
        }
    }

    private async Task SendWelcomeAndStateAsync(CancellationToken cancellationToken)
    {
        await publisher.SendControlAsync(
            "welcome",
            "session.welcome",
            new WelcomePayload(
                typeof(LocalWebSocketServer).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                eventStore.AgentInstanceId,
                coordinator.AgentSessionId,
                ["sip.register", "call.outbound", "call.inbound", "call.dtmf", "event.durable"],
                eventStore.LastSequence,
                eventStore.LastAcknowledgedSequence),
            cancellationToken);
        await SendSnapshotAsync(cancellationToken);
    }

    private async Task SendSnapshotAsync(CancellationToken cancellationToken)
    {
        AgentSnapshotPayload snapshot = await coordinator.GetSnapshotAsync(cancellationToken);
        await publisher.SendControlAsync("snapshot", "agent.snapshot", snapshot, cancellationToken);
    }

    private async Task ReplayPendingEventsAsync(CancellationToken cancellationToken)
    {
        long afterSequence = eventStore.LastAcknowledgedSequence;
        while (true)
        {
            IReadOnlyList<StoredDurableEvent> page = await eventStore.LoadPendingAsync(
                afterSequence,
                250,
                cancellationToken);
            if (page.Count == 0)
            {
                return;
            }

            foreach (StoredDurableEvent storedEvent in page)
            {
                await publisher.PublishDurableAsync(storedEvent, cancellationToken);
                afterSequence = storedEvent.Sequence;
            }
        }
    }

    private static async Task<ProtocolEnvelope> ReceiveAsync(WebSocket socket, CancellationToken applicationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        timeout.CancelAfter(ProtocolConstants.ClientTimeout);
        byte[] chunk = new byte[8 * 1024];
        using var buffer = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new OperationCanceledException("Client closed the WebSocket.");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new ProtocolException("invalid_message", "Only text messages are supported.");
            }

            if (buffer.Length + result.Count > ProtocolConstants.MaximumMessageBytes)
            {
                throw new ProtocolException("message_too_large", "Message exceeds the maximum size.");
            }

            buffer.Write(chunk, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return ProtocolCodec.Deserialize(buffer.ToArray());
    }

    private static void ValidateHello(ProtocolEnvelope hello)
    {
        if (hello.Kind != "hello" || hello.Type != "session.hello")
        {
            throw new ProtocolException("invalid_message", "The first message must be session.hello.");
        }
    }
}
