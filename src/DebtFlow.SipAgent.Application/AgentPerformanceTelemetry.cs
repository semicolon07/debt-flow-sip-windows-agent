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
}
