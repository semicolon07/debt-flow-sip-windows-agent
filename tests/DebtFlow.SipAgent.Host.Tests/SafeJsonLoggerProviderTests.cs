using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using DebtFlow.SipAgent.Application;
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

    [Fact]
    public async Task WriterFailure_IsObservableAndWriterRecoversWhenDirectoryReturns()
    {
        string root = Path.Combine(Path.GetTempPath(), $"sip-agent-logger-recovery-{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "logs");
        SafeJsonLoggerProvider? provider = null;
        try
        {
            var failureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == AgentPerformanceTelemetry.MeterName &&
                    instrument.Name == "sip_agent.logging.writer_failures")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                if (instrument.Name == "sip_agent.logging.writer_failures" && measurement > 0)
                {
                    failureObserved.TrySetResult();
                }
            });
            listener.Start();

            Directory.CreateDirectory(directory);
            provider = new SafeJsonLoggerProvider(directory, writeConsole: false);
            ILogger logger = provider.CreateLogger("test");
            Directory.Delete(directory);
            await File.WriteAllTextAsync(directory, "temporarily blocks the log directory");

            logger.LogInformation("recoverable-log-line");
            await failureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => provider.GetOperationalHealth().WriterFailures > 0,
                TimeSpan.FromSeconds(2));

            File.Delete(directory);
            Directory.CreateDirectory(directory);
            await WaitForLogLineAsync(
                directory,
                "recoverable-log-line",
                TimeSpan.FromSeconds(10));
            provider.Dispose();
            provider = null;

            string content = string.Join(
                Environment.NewLine,
                Directory.GetFiles(directory, "*.jsonl").Select(File.ReadAllText));
            Assert.Contains("recoverable-log-line", content, StringComparison.Ordinal);
        }
        finally
        {
            provider?.Dispose();
            if (File.Exists(directory))
            {
                File.Delete(directory);
            }
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task WaitForLogLineAsync(
        string directory,
        string expected,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!cancellation.IsCancellationRequested)
        {
            foreach (string path in Directory.GetFiles(directory, "*.jsonl"))
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(stream);
                string content = await reader.ReadToEndAsync();
                if (content.Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellation.Token)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        throw new TimeoutException("The recovered logger did not persist the pending log line.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition() && !cancellation.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        Assert.True(condition(), "The logger provider did not report its own writer failure.");
    }
}
