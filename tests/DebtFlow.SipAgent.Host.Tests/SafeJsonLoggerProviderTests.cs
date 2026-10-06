using Microsoft.Extensions.Logging;
using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class SafeJsonLoggerProviderTests
{
    [Fact]
    public void StructuredSensitiveProperties_AreRedactedFromJsonLine()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-logs-{Guid.NewGuid():N}");
        try
        {
            var provider = new SafeJsonLoggerProvider(directory, writeConsole: false);
            ILogger logger = provider.CreateLogger("test");

            logger.LogInformation(
                "Operation used {Password}, {Username}, {ContextToken}, {Destination}, {DtmfDigit}, {Origin} and safe {Code}",
                "secret-value",
                "sip-user-42",
                "opaque-context-token",
                "0812345678",
                "9",
                "https://portal.customer.example",
                "registration_failed");
            logger.Log(
                LogLevel.Warning,
                new EventId(7),
                "password=plain-secret destination=0899999999",
                null,
                static (state, _) => state);
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(provider));
            using var sipProvider = new RedactingSipLoggerProvider(loggerFactory);
            ILogger sipLogger = sipProvider.CreateLogger("SIPSorcery.SIP");
            sipLogger.LogWarning("Raw library state {Authorization}", "Digest another-secret");
            provider.Dispose();

            string content = File.ReadAllText(Assert.Single(Directory.GetFiles(directory, "*.jsonl")));
            Assert.DoesNotContain("secret-value", content, StringComparison.Ordinal);
            Assert.DoesNotContain("sip-user-42", content, StringComparison.Ordinal);
            Assert.DoesNotContain("opaque-context-token", content, StringComparison.Ordinal);
            Assert.DoesNotContain("0812345678", content, StringComparison.Ordinal);
            Assert.DoesNotContain("plain-secret", content, StringComparison.Ordinal);
            Assert.DoesNotContain("0899999999", content, StringComparison.Ordinal);
            Assert.DoesNotContain("another-secret", content, StringComparison.Ordinal);
            Assert.DoesNotContain("portal.customer.example", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Raw library state", content, StringComparison.Ordinal);
            Assert.DoesNotContain("\"9\"", content, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
            Assert.Contains("registration_failed", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
