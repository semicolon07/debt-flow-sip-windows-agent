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
    private const int MaximumLogTailBytes = 2 * 1024 * 1024;

    public async Task<string> ExportAsync(
        string outputPath,
        AgentSnapshotPayload? snapshot,
        EventStoreHealth? storage,
        int? captureDeviceCount,
        int? playbackDeviceCount,
        bool localConfigurationValid,
        CancellationToken cancellationToken,
        int tlsCertificateProfileVersion = LocalTlsCertificateProfile.Version,
        int? tlsCertificateDaysRemaining = null,
        string? tlsCertificateErrorCode = null,
        AgentOperationalHealth? operationalHealth = null,
        WebSocketOperationalHealth? webSocketHealth = null,
        LoggerOperationalHealth? loggerHealth = null,
        IReadOnlyCollection<string>? collectionErrorCodes = null)
    {
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A diagnostic output directory is required.", nameof(outputPath));
        }

        Directory.CreateDirectory(directory);
        LogCollectionResult logs = await CollectLogsAsync(cancellationToken);
        string[] errors = (collectionErrorCodes ?? [])
            .Concat(logs.ErrorCodes)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
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
                            snapshotAvailable = snapshot != null,
                            storageHealthAvailable = storage != null,
                            collectionErrorCodes = errors,
                            logFilesDiscovered = logs.DiscoveredCount,
                            logFilesIncluded = logs.Files.Count,
                            logFilesSkipped = logs.DiscoveredCount - logs.Files.Count,
                            agentState = snapshot?.AgentState,
                            agentStateCode = snapshot?.AgentStateCode,
                            registrationState = snapshot?.RegistrationState,
                            audioState = snapshot?.AudioState,
                            activeCallCount = snapshot?.ActiveCalls.Count,
                            activeCallStates = snapshot?.ActiveCalls.Select(call => call.State).ToArray() ?? [],
                            lastSequence = snapshot?.LastSequence,
                            lastAcknowledgedSequence = snapshot?.LastAcknowledgedSequence,
                            pendingEventCount = storage?.PendingEventCount,
                            storageBytes = storage?.StorageBytes,
                            storageState = storage?.CapacityState.ToString().ToLowerInvariant(),
                            regularEventSlotsRemaining = storage?.RegularEventSlotsRemaining,
                            terminalReserveAvailable = storage?.TerminalReserveAvailable,
                            regularStorageBytesRemaining = storage?.RegularStorageBytesRemaining,
                            captureDeviceCount,
                            playbackDeviceCount,
                            localConfigurationValid,
                            tlsCertificateProfileVersion,
                            tlsCertificateDaysRemaining,
                            tlsCertificateErrorCode,
                            coordinatorProcessorState = operationalHealth?.CoordinatorProcessorState,
                            sipSignalPumpState = operationalHealth?.SipSignalPumpState,
                            coordinatorQueueDepth = operationalHealth?.CoordinatorQueueDepth,
                            coordinatorQueueHighWater = operationalHealth?.CoordinatorQueueHighWater,
                            activeBackgroundTasks = operationalHealth?.ActiveBackgroundTasks,
                            backgroundTaskFaultCount = operationalHealth?.BackgroundTaskFaultCount,
                            lastBackgroundFaultOperation = operationalHealth?.LastBackgroundFaultOperation,
                            sipSignalFailureCount = operationalHealth?.SipSignalFailureCount,
                            lastSipSignalFailureCode = operationalHealth?.LastSipSignalFailureCode,
                            lifetimeCancellationRequested = operationalHealth?.LifetimeCancellationRequested,
                            webSocketSessionGeneration = webSocketHealth?.SessionGeneration,
                            webSocketConnected = webSocketHealth?.Connected,
                            webSocketControlQueueDepth = webSocketHealth?.ControlQueueDepth,
                            webSocketDurableQueueDepth = webSocketHealth?.DurableQueueDepth,
                            webSocketRealtimeQueueDepth = webSocketHealth?.RealtimeQueueDepth,
                            webSocketActivationQueueDepth = webSocketHealth?.ActivationQueueDepth,
                            webSocketDeferredQueueDepth = webSocketHealth?.DeferredQueueDepth,
                            webSocketControlQueueHighWater = webSocketHealth?.ControlQueueHighWater,
                            webSocketDurableQueueHighWater = webSocketHealth?.DurableQueueHighWater,
                            webSocketRealtimeQueueHighWater = webSocketHealth?.RealtimeQueueHighWater,
                            webSocketActivationQueueHighWater = webSocketHealth?.ActivationQueueHighWater,
                            webSocketDeferredQueueHighWater = webSocketHealth?.DeferredQueueHighWater,
                            webSocketDroppedRealtimeEvents = webSocketHealth?.DroppedRealtimeEvents,
                            webSocketQueueAbortCount = webSocketHealth?.QueueAbortCount,
                            webSocketWriterFaultCount = webSocketHealth?.WriterFaultCount,
                            loggerQueueDepth = loggerHealth?.QueueDepth,
                            loggerQueueHighWater = loggerHealth?.QueueHighWater,
                            loggerDroppedRecords = loggerHealth?.DroppedRecords,
                            loggerWriterFailures = loggerHealth?.WriterFailures
                        },
                        ProtocolJson.Options,
                        cancellationToken);
                }

                foreach (CollectedLogFile log in logs.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ZipArchiveEntry logEntry = archive.CreateEntry($"logs/{log.Name}", CompressionLevel.Optimal);
                    await using Stream entryStream = logEntry.Open();
                    await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
                    foreach (string line in log.Lines)
                    {
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
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

    private async Task<LogCollectionResult> CollectLogsAsync(CancellationToken cancellationToken)
    {
        var files = new List<CollectedLogFile>();
        var errors = new List<string>();
        FileInfo[] candidates;
        try
        {
            candidates = Directory.Exists(logDirectory)
                ? new DirectoryInfo(logDirectory)
                    .GetFiles("agent-*.jsonl")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(MaximumLogFiles)
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException && OperationalExceptionPolicy.IsNonFatal(exception))
        {
            return new LogCollectionResult([], 0, ["log_directory_unavailable"]);
        }

        foreach (FileInfo candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                files.Add(new CollectedLogFile(
                    candidate.Name,
                    await ReadLogTailAsync(candidate.FullName, cancellationToken)));
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && OperationalExceptionPolicy.IsNonFatal(exception))
            {
                errors.Add("log_file_unavailable");
            }
        }

        return new LogCollectionResult(files, candidates.Length, errors);
    }

    private static async Task<IReadOnlyList<string>> ReadLogTailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long capturedLength = stream.Length;
        long offset = Math.Max(0, capturedLength - MaximumLogTailBytes);
        int bytesToRead = checked((int)(capturedLength - offset));
        stream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(bytesToRead);
        int bytesRead = 0;
        while (bytesRead < bytesToRead)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(bytesRead, bytesToRead - bytesRead), cancellationToken);
            if (count == 0)
            {
                break;
            }

            bytesRead += count;
        }

        using var reader = new StringReader(Encoding.UTF8.GetString(buffer, 0, bytesRead));
        if (offset > 0)
        {
            _ = reader.ReadLine();
        }

        var lines = new Queue<string>(MaximumLogLinesPerFile);
        while (reader.ReadLine() is { } line)
        {
            if (lines.Count == MaximumLogLinesPerFile)
            {
                lines.Dequeue();
            }

            lines.Enqueue(SafeLogSanitizer.Sanitize(line));
        }

        return lines.ToArray();
    }

    private sealed record CollectedLogFile(string Name, IReadOnlyList<string> Lines);

    private sealed record LogCollectionResult(
        IReadOnlyList<CollectedLogFile> Files,
        int DiscoveredCount,
        IReadOnlyList<string> ErrorCodes);
}
