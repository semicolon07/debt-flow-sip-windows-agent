using System.IO.Compression;
using System.Text;
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
                CancellationToken.None);

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
            Assert.Contains("[REDACTED]", combined, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
