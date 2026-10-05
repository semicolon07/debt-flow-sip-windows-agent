using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DebtFlow.SipAgent.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class WindowsLocalTlsCertificateIntegrationTests
{
    [Fact]
    public async Task Current_user_certificate_supports_trusted_wss_and_rejects_plaintext_ws()
    {
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(Path.GetTempPath(), $"debt-flow-tls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var metadataStore = new FileLocalTlsCertificateMetadataStore(Path.Combine(directory, "metadata.json"));
        var manager = new LocalTlsCertificateManager(
            new WindowsLocalTlsCertificatePlatform(),
            metadataStore,
            new SystemLocalTlsClock());
        LocalTlsCertificateResult provisioned = await manager.RepairAsync(CancellationToken.None);
        Assert.True(provisioned.IsReady, provisioned.ErrorCode);
        Assert.NotNull(provisioned.Certificate);
        string keyIdentity = metadataStore.Load()!.KeyContainerIdentity;

        try
        {
            using X509Certificate2 certificate = provisioned.Certificate;
            using RSA privateKey = certificate.GetRSAPrivateKey()
                ?? throw new InvalidOperationException("test_private_key_missing");
            Assert.ThrowsAny<CryptographicException>(() => privateKey.ExportPkcs8PrivateKey());

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(server =>
                server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            await using WebApplication app = builder.Build();
            app.UseWebSockets();
            app.Map("/agent/v1", async context =>
            {
                using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test_complete", context.RequestAborted);
            });
            await app.StartAsync();
            string httpsAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var secureUri = new Uri(httpsAddress.Replace("https://", "wss://", StringComparison.Ordinal) + "/agent/v1");
            var plaintextUri = new Uri(httpsAddress.Replace("https://", "ws://", StringComparison.Ordinal) + "/agent/v1");

            using var secure = new ClientWebSocket();
            await secure.ConnectAsync(secureUri, CancellationToken.None);
            WebSocketReceiveResult close = await secure.ReceiveAsync(new byte[1], CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Close, close.MessageType);

            using var plaintext = new ClientWebSocket();
            await Assert.ThrowsAnyAsync<Exception>(() => plaintext.ConnectAsync(plaintextUri, CancellationToken.None));
            await app.StopAsync();
        }
        finally
        {
            LocalTlsCertificateResult removed = await manager.RemoveAsync(CancellationToken.None);
            removed.Certificate?.Dispose();
            Assert.Null(removed.ErrorCode);
            Assert.False(CngKey.Exists(keyIdentity, CngProvider.MicrosoftSoftwareKeyStorageProvider));
            Directory.Delete(directory, recursive: true);
        }
    }
}
