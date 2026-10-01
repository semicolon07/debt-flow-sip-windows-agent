using System.Text.Json;
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
    string DataJson);

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
    string DataJson)
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
            document.RootElement.Clone());
    }
}

public sealed record ProcessedCommand(
    string CommandId,
    string CommandType,
    string RequestHash,
    string ResultJson,
    DateTimeOffset ProcessedAtUtc);

public interface IAgentEventStore : IAsyncDisposable
{
    string AgentInstanceId { get; }
    long LastSequence { get; }
    long LastAcknowledgedSequence { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoredDurableEvent> AppendAsync(DurableEventDraft draft, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredDurableEvent>> LoadPendingAsync(
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken);
    Task<long> CountPendingAsync(CancellationToken cancellationToken);
    Task AcknowledgeThroughAsync(long sequence, CancellationToken cancellationToken);
    Task<ProcessedCommand?> FindCommandAsync(string commandId, CancellationToken cancellationToken);
    Task SaveCommandAsync(ProcessedCommand command, CancellationToken cancellationToken);
}

public interface IAgentEventPublisher
{
    Task PublishDurableAsync(StoredDurableEvent storedEvent, CancellationToken cancellationToken);
    Task PublishRealtimeAsync(string eventType, RealtimeEventPayload payload, CancellationToken cancellationToken);
}

public interface ISipRuntime : IAsyncDisposable
{
    event Func<SipSignal, Task>? Signal;
    string AudioState { get; }
    Task ConfigureAsync(SipConfiguration configuration, CancellationToken cancellationToken);
    Task StartRegistrationAsync(CancellationToken cancellationToken);
    Task StopRegistrationAsync(CancellationToken cancellationToken);
    Task StartCallAsync(string destination, CancellationToken cancellationToken);
    Task AnswerAsync(CancellationToken cancellationToken);
    Task RejectAsync(CancellationToken cancellationToken);
    Task RejectUnavailableAsync(CancellationToken cancellationToken);
    Task HangupAsync(CancellationToken cancellationToken);
    Task SendDtmfAsync(char digit, CancellationToken cancellationToken);
}

public sealed record SipConfiguration(string Host, int Port, string Username, string Password);

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
    DtmfReceived
}

public sealed record SipSignal(
    SipSignalType Type,
    int? SipStatusCode = null,
    string? SafeCode = null,
    string? Caller = null,
    string? Codec = null);
