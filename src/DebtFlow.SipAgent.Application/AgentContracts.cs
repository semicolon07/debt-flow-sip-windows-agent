using System.Text.Json;
using System.Threading.Channels;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Application;

public interface IAgentClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IAgentDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemAgentDelay : IAgentDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed class SystemAgentClock : IAgentClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IAgentIdGenerator
{
    string NewId();
}

public sealed class GuidAgentIdGenerator : IAgentIdGenerator
{
    public string NewId() => ProtocolCodec.NewId();
}

public sealed record DurableEventDraft(
    string EventId,
    string AgentSessionId,
    string CallId,
    string? CommandId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string? State,
    string DataJson,
    string? CollectionId = null,
    string? CollectionBindingId = null,
    string? CallContextId = null);

public sealed record StoredDurableEvent(
    long Sequence,
    string EventId,
    string AgentInstanceId,
    string AgentSessionId,
    string CallId,
    string? CommandId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string? State,
    string DataJson,
    string? CollectionId = null,
    string? CollectionBindingId = null,
    string? CallContextId = null)
{
    public DurableEventPayload ToPayload()
    {
        using JsonDocument document = JsonDocument.Parse(DataJson);
        return new DurableEventPayload(
            "durable",
            EventId,
            AgentInstanceId,
            AgentSessionId,
            Sequence,
            CallId,
            CommandId,
            OccurredAtUtc,
            State,
            document.RootElement.Clone(),
            CollectionId,
            CollectionBindingId,
            CallContextId);
    }
}

public sealed record ProcessedCommand(
    string CommandId,
    string CommandType,
    string RequestHash,
    string ResultJson,
    DateTimeOffset ProcessedAtUtc,
    string ExecutionState = "completed");

public enum EventStoreCapacityState
{
    Healthy,
    Warning,
    Critical,
    Full
}

public sealed record EventStoreHealth(
    long PendingEventCount,
    long StorageBytes,
    EventStoreCapacityState CapacityState,
    DateTimeOffset? OldestPendingAtUtc = null);

public sealed record AgentOperationalHealth(
    string CoordinatorProcessorState,
    string SipSignalPumpState,
    int CoordinatorQueueDepth,
    int CoordinatorQueueHighWater,
    int ActiveBackgroundTasks,
    long BackgroundTaskFaultCount,
    string? LastBackgroundFaultOperation,
    long SipSignalFailureCount,
    string? LastSipSignalFailureCode,
    bool LifetimeCancellationRequested);

public sealed class AgentStoreException(string errorCode, Exception? innerException = null)
    : Exception(errorCode, innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public interface IAgentEventStore : IAsyncDisposable
{
    string AgentInstanceId { get; }
    long LastSequence { get; }
    long LastAcknowledgedSequence { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredDurableEvent> AppendAsync(DurableEventDraft draft, CancellationToken cancellationToken);
    Task<StoredDurableEvent> AppendCallEventAsync(
        DurableEventDraft draft,
        CallSessionState callState,
        bool terminal,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredDurableEvent>> AppendCallTransitionAsync(
        IReadOnlyList<DurableEventDraft> drafts,
        CallSessionState callState,
        bool terminal,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredDurableEvent>> LoadPendingAsync(
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken);
    Task<long> CountPendingAsync(CancellationToken cancellationToken);
    Task<EventStoreHealth> GetHealthAsync(CancellationToken cancellationToken);
    Task<CallSessionState?> LoadActiveCallAsync(CancellationToken cancellationToken);
    Task AcknowledgeThroughAsync(long sequence, CancellationToken cancellationToken);
    Task<ProcessedCommand?> FindCommandAsync(string commandId, CancellationToken cancellationToken);
    Task<bool> HasCommandRequestHashPrefixAsync(string prefix, CancellationToken cancellationToken);
    Task SaveCommandAsync(ProcessedCommand command, CancellationToken cancellationToken);
    Task RecoverExecutingCommandsAsync(DateTimeOffset recoveredAtUtc, CancellationToken cancellationToken);
    Task PruneCommandsAsync(DateTimeOffset olderThanUtc, int maximumRetained, CancellationToken cancellationToken);
    Task CheckpointAsync(CancellationToken cancellationToken);
}

public interface IAgentEventPublisher
{
    Task PublishDurableAsync(StoredDurableEvent storedEvent, CancellationToken cancellationToken);
    Task PublishRealtimeAsync(string eventType, RealtimeEventPayload payload, CancellationToken cancellationToken);
}

public interface ISipRuntime : IAsyncDisposable
{
    ChannelReader<SipSignal> Signals { get; }
    string AudioState { get; }
    bool IsMicrophoneMuted { get; }
    int OutputVolume { get; }
    int InputVolume { get; }
    AudioDevicesSnapshot AudioDevices { get; }
    Task ConfigureAsync(SipConfiguration configuration, CancellationToken cancellationToken);
    Task StartRegistrationAsync(long generation, CancellationToken cancellationToken);
    Task StopRegistrationAsync(long generation, CancellationToken cancellationToken);
    Task StartCallAsync(SipCallHandle call, string destination, CancellationToken cancellationToken);
    Task AnswerAsync(SipCallHandle call, CancellationToken cancellationToken);
    Task RejectAsync(SipCallHandle call, CancellationToken cancellationToken);
    Task RejectUnavailableAsync(SipCallHandle call, CancellationToken cancellationToken);
    Task HangupAsync(SipCallHandle call, CancellationToken cancellationToken);
    Task SendDtmfAsync(SipCallHandle call, char digit, CancellationToken cancellationToken);
    Task SetMicrophoneMutedAsync(SipCallHandle call, bool muted, CancellationToken cancellationToken);
    Task SetOutputVolumeAsync(SipCallHandle call, int volume, CancellationToken cancellationToken);
    Task SetInputVolumeAsync(SipCallHandle call, int volume, CancellationToken cancellationToken);
    Task SetOutputVolumePreferenceAsync(int volume, CancellationToken cancellationToken);
    Task SetInputVolumePreferenceAsync(int volume, CancellationToken cancellationToken);
    Task SetAudioDevicePreferencesAsync(
        string outputDeviceId,
        string inputDeviceId,
        CancellationToken cancellationToken);
    Task TestOutputDeviceAsync(string deviceId, CancellationToken cancellationToken);
    Task<int> TestInputDeviceAsync(string deviceId, CancellationToken cancellationToken);
}

public sealed record AudioPreferences(
    int OutputVolume,
    int InputVolume,
    string OutputDeviceId,
    string InputDeviceId)
{
    public const string SystemDefaultDeviceId = "system-default";
    public static AudioPreferences Default { get; } =
        new(100, 100, SystemDefaultDeviceId, SystemDefaultDeviceId);
}

public interface IAudioPreferencesStore
{
    AudioPreferences Load();
    void Save(AudioPreferences preferences);
}

public interface IRegistrationRetryPolicy
{
    TimeSpan GetDelay(int attempt);
}

public sealed class JitteredRegistrationRetryPolicy : IRegistrationRetryPolicy
{
    public TimeSpan GetDelay(int attempt)
    {
        int exponent = Math.Clamp(attempt - 1, 0, 5);
        double maximumSeconds = Math.Min(60, 2 * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(Math.Max(100, maximumSeconds * 1000 * Random.Shared.NextDouble()));
    }
}

public sealed record SipConfiguration(string Host, int Port, string Username, string Password);

public sealed record SipCallHandle(string RuntimeCallId, long Generation, string? PublicCallId = null);

public enum SipSignalType
{
    RegistrationRegistering,
    RegistrationRegistered,
    RegistrationUnregistered,
    RegistrationFailed,
    IncomingCall,
    CallTrying,
    CallRinging,
    CallConnected,
    CallFailed,
    CallRemoteEnded,
    IncomingCancelled,
    MediaReady,
    MediaDegraded,
    AudioInventoryChanged,
    DtmfReceived,
    RuntimeFailed
}

public sealed record SipSignal(
    SipSignalType Type,
    int? SipStatusCode = null,
    string? SafeCode = null,
    string? Caller = null,
    string? Codec = null,
    bool Retryable = false,
    SipCallHandle? Call = null,
    long? RegistrationGeneration = null,
    TaskCompletionSource<bool>? ProcessingCompletion = null);
