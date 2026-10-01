using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class DiagnosticBundleExporter(string logDirectory)
{
    private const int MaximumLogFiles = 3;
    private const int MaximumLogLinesPerFile = 512;

    public async Task<string> ExportAsync(
        string outputPath,
        AgentSnapshotPayload snapshot,
        EventStoreHealth storage,
        int captureDeviceCount,
        int playbackDeviceCount,
        bool localConfigurationValid,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A diagnostic output directory is required.", nameof(outputPath));
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                ZipArchiveEntry summaryEntry = archive.CreateEntry("summary.json", CompressionLevel.Optimal);
                await using (Stream summaryStream = summaryEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(
                        summaryStream,
                        new
                        {
                            exportedAtUtc = DateTimeOffset.UtcNow,
                            version = typeof(DiagnosticBundleExporter).Assembly.GetName().Version?.ToString() ?? "unknown",
                            agentState = snapshot.AgentState,
                            agentStateCode = snapshot.AgentStateCode,
                            registrationState = snapshot.RegistrationState,
                            audioState = snapshot.AudioState,
                            activeCallCount = snapshot.ActiveCalls.Count,
                            pendingEventCount = storage.PendingEventCount,
                            storageBytes = storage.StorageBytes,
                            storageState = storage.CapacityState.ToString().ToLowerInvariant(),
                            captureDeviceCount,
                            playbackDeviceCount,
                            localConfigurationValid
                        },
                        ProtocolJson.Options,
                        cancellationToken);
                }

                if (Directory.Exists(logDirectory))
                {
                    foreach (FileInfo log in new DirectoryInfo(logDirectory)
                                 .GetFiles("agent-*.jsonl")
                                 .OrderByDescending(file => file.LastWriteTimeUtc)
                                 .Take(MaximumLogFiles))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string[] lines = await File.ReadAllLinesAsync(log.FullName, cancellationToken);
                        IEnumerable<string> bounded = lines
                            .TakeLast(MaximumLogLinesPerFile)
                            .Select(SafeLogSanitizer.Sanitize);
                        ZipArchiveEntry logEntry = archive.CreateEntry($"logs/{log.Name}", CompressionLevel.Optimal);
                        await using Stream entryStream = logEntry.Open();
                        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
                        foreach (string line in bounded)
                        {
                            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                        }
                    }
                }
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
