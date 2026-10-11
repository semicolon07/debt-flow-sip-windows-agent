using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Host;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class DiagnosticBundleExporterTests
{
    [Fact]
    public async Task ExportAsync_IncludesBoundedSummaryAndRedactsSensitiveLogText()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-diagnostics-{Guid.NewGuid():N}");
        string logs = Path.Combine(directory, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "agent-20261001.jsonl"),
            "password=plain-secret username=sip-user destination=0812345678 origin=https://portal.customer.example safe=registration_failed");
        string output = Path.Combine(directory, "bundle.zip");
        try
        {
            var exporter = new DiagnosticBundleExporter(logs);
            await exporter.ExportAsync(
                output,
                new AgentSnapshotPayload("ready", "registered", "ready", [], 4, 12),
                new EventStoreHealth(4, 4096, EventStoreCapacityState.Healthy),
                1,
                1,
                true,
                CancellationToken.None,
                tlsCertificateProfileVersion: 1,
                tlsCertificateDaysRemaining: 24,
                tlsCertificateErrorCode: "tls_certificate_rotation_failed",
                operationalHealth: new AgentOperationalHealth(
                    "running",
                    "running",
                    2,
                    8,
                    1,
                    0,
                    null,
                    0,
                    null,
                    false),
                webSocketHealth: new WebSocketOperationalHealth(
                    3,
                    true,
                    1,
                    2,
                    3,
                    4,
                    5,
                    6,
                    7,
                    8,
                    9,
                    10,
                    11,
                    0,
                    0),
                loggerHealth: new LoggerOperationalHealth(1, 9, 0, 0));

            using ZipArchive archive = ZipFile.OpenRead(output);
            Assert.NotNull(archive.GetEntry("summary.json"));
            Assert.Equal(2, archive.Entries.Count);
            string combined = string.Join(
                "\n",
                archive.Entries.Select(entry =>
                {
                    using Stream stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return reader.ReadToEnd();
                }));
            Assert.DoesNotContain("plain-secret", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("sip-user", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("0812345678", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("portal.customer.example", combined, StringComparison.Ordinal);
            Assert.Contains("registration_failed", combined, StringComparison.Ordinal);
            Assert.Contains("tls_certificate_rotation_failed", combined, StringComparison.Ordinal);
            Assert.Contains("\"tlsCertificateDaysRemaining\":24", combined, StringComparison.Ordinal);
            Assert.Contains("\"sipSignalPumpState\":\"running\"", combined, StringComparison.Ordinal);
            Assert.Contains("\"webSocketRealtimeQueueHighWater\":8", combined, StringComparison.Ordinal);
            Assert.Contains("\"loggerQueueHighWater\":9", combined, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", combined, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExportAsync_WhenCurrentLogIsOpenForWriting_StillIncludesLogTail()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-diagnostics-{Guid.NewGuid():N}");
        string logs = Path.Combine(directory, "logs");
        Directory.CreateDirectory(logs);
        string logPath = Path.Combine(logs, "agent-20261001.jsonl");
        await using var activeWriter = new FileStream(
            logPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        await activeWriter.WriteAsync(Encoding.UTF8.GetBytes("safe=remote_hangup_received\n"));
        await activeWriter.FlushAsync();
        string output = Path.Combine(directory, "bundle.zip");

        try
        {
            var exporter = new DiagnosticBundleExporter(logs);
            await exporter.ExportAsync(
                output,
                new AgentSnapshotPayload("ready", "registered", "ready", [], 0, 8),
                new EventStoreHealth(0, 4096, EventStoreCapacityState.Healthy),
                1,
                1,
                true,
                CancellationToken.None);

            using ZipArchive archive = ZipFile.OpenRead(output);
            ZipArchiveEntry logEntry = Assert.Single(
                archive.Entries,
                entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal));
            using Stream stream = logEntry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Contains("remote_hangup_received", await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await activeWriter.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExportAsync_WhenRuntimeStateIsUnavailable_StillWritesSummary()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-diagnostics-{Guid.NewGuid():N}");
        string output = Path.Combine(directory, "bundle.zip");
        try
        {
            var exporter = new DiagnosticBundleExporter(Path.Combine(directory, "missing-logs"));
            await exporter.ExportAsync(
                output,
                snapshot: null,
                storage: null,
                captureDeviceCount: null,
                playbackDeviceCount: null,
                localConfigurationValid: true,
                CancellationToken.None,
                collectionErrorCodes: ["snapshot_unavailable", "storage_health_unavailable"]);

            using ZipArchive archive = ZipFile.OpenRead(output);
            ZipArchiveEntry summaryEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("summary.json"));
            using Stream stream = summaryEntry.Open();
            using JsonDocument summary = await JsonDocument.ParseAsync(stream);
            JsonElement root = summary.RootElement;
            Assert.False(root.GetProperty("snapshotAvailable").GetBoolean());
            Assert.False(root.GetProperty("storageHealthAvailable").GetBoolean());
            Assert.Contains(
                root.GetProperty("collectionErrorCodes").EnumerateArray().Select(value => value.GetString()),
                value => value == "snapshot_unavailable");
            Assert.Contains(
                root.GetProperty("collectionErrorCodes").EnumerateArray().Select(value => value.GetString()),
                value => value == "storage_health_unavailable");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
