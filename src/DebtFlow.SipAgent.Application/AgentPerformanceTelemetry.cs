using System.Diagnostics.Metrics;

namespace DebtFlow.SipAgent.Application;

public static class AgentPerformanceTelemetry
{
    public const string MeterName = "DebtFlow.SipAgent";

    private static readonly Meter Meter = new(
        MeterName,
        typeof(AgentPerformanceTelemetry).Assembly.GetName().Version?.ToString() ?? "unknown");
    private static readonly Histogram<double> CoordinatorQueueWait = Meter.CreateHistogram<double>(
        "sip_agent.coordinator.queue_wait",
        unit: "ms");
    private static readonly Histogram<double> StorageOperationDuration = Meter.CreateHistogram<double>(
        "sip_agent.storage.operation.duration",
        unit: "ms");
    private static readonly Histogram<double> AudioProbeDuration = Meter.CreateHistogram<double>(
        "sip_agent.audio.device_probe.duration",
        unit: "ms");
    private static readonly Histogram<double> ReplayDuration = Meter.CreateHistogram<double>(
        "sip_agent.websocket.replay.duration",
        unit: "ms");
    private static readonly Counter<long> ReplayEvents = Meter.CreateCounter<long>(
        "sip_agent.websocket.replay.events",
        unit: "{event}");
    private static readonly Counter<long> DroppedLogRecords = Meter.CreateCounter<long>(
        "sip_agent.logging.dropped_records",
        unit: "{record}");
    private static readonly Counter<long> LogWriterFailures = Meter.CreateCounter<long>(
        "sip_agent.logging.writer_failures",
        unit: "{failure}");
    private static readonly Counter<long> BackgroundTaskFaults = Meter.CreateCounter<long>(
        "sip_agent.background_task.faults",
        unit: "{fault}");
    private static readonly Counter<long> SipSignalFailures = Meter.CreateCounter<long>(
        "sip_agent.sip_signal.failures",
        unit: "{failure}");
    private static readonly Counter<long> PublisherFailures = Meter.CreateCounter<long>(
        "sip_agent.publisher.failures",
        unit: "{failure}");
    private static readonly Counter<long> WebSocketQueueOverflows = Meter.CreateCounter<long>(
        "sip_agent.websocket.queue_overflows",
        unit: "{overflow}");
    private static readonly Counter<long> DroppedRealtimeEvents = Meter.CreateCounter<long>(
        "sip_agent.websocket.realtime.dropped_events",
        unit: "{event}");
    private static readonly Counter<long> CommandPruneFailures = Meter.CreateCounter<long>(
        "sip_agent.command_prune.failures",
        unit: "{failure}");
    private static readonly Histogram<int> QueueDepthHighWater = Meter.CreateHistogram<int>(
        "sip_agent.queue.high_water_mark",
        unit: "{message}");

    public static void RecordCoordinatorQueueWait(TimeSpan elapsed) =>
        CoordinatorQueueWait.Record(Math.Max(0, elapsed.TotalMilliseconds));

    public static void RecordStorageOperation(string operation, TimeSpan elapsed) =>
        StorageOperationDuration.Record(
            Math.Max(0, elapsed.TotalMilliseconds),
            new KeyValuePair<string, object?>("operation", operation));

    public static void RecordAudioProbe(TimeSpan elapsed, bool cacheHit) =>
        AudioProbeDuration.Record(
            Math.Max(0, elapsed.TotalMilliseconds),
            new KeyValuePair<string, object?>("cache_hit", cacheHit));

    public static void RecordReplay(TimeSpan elapsed, long eventCount)
    {
        ReplayDuration.Record(Math.Max(0, elapsed.TotalMilliseconds));
        ReplayEvents.Add(Math.Max(0, eventCount));
    }

    public static void RecordDroppedLogs(long count)
    {
        if (count > 0)
        {
            DroppedLogRecords.Add(count);
        }
    }

    public static void RecordLogWriterFailure(string errorType) =>
        LogWriterFailures.Add(
            1,
            new KeyValuePair<string, object?>("error_type", errorType));

    public static void RecordBackgroundTaskFault(string operation) =>
        BackgroundTaskFaults.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public static void RecordSipSignalFailure(string signalType) =>
        SipSignalFailures.Add(1, new KeyValuePair<string, object?>("signal_type", signalType));

    public static void RecordPublisherFailure(string channel) =>
        PublisherFailures.Add(1, new KeyValuePair<string, object?>("channel", channel));

    public static void RecordWebSocketQueueOverflow(string lane) =>
        WebSocketQueueOverflows.Add(1, new KeyValuePair<string, object?>("lane", lane));

    public static void RecordDroppedRealtimeEvent() => DroppedRealtimeEvents.Add(1);

    public static void RecordCommandPruneFailure() => CommandPruneFailures.Add(1);

    public static void RecordQueueHighWater(string queue, int depth) =>
        QueueDepthHighWater.Record(
            Math.Max(0, depth),
            new KeyValuePair<string, object?>("queue", queue));
}
