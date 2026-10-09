using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class UnavailableAgentEventStore : IAgentEventStore
{
    private readonly string _failureCode;

    public UnavailableAgentEventStore(string failureCode = "outbox_unavailable")
    {
        _failureCode = failureCode;
    }

    public string AgentInstanceId { get; } = ProtocolCodec.NewId();
    public long LastSequence => 0;
    public long LastAcknowledgedSequence => 0;

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<StoredDurableEvent> AppendAsync(DurableEventDraft draft, CancellationToken cancellationToken) =>
        Task.FromException<StoredDurableEvent>(new AgentStoreException(_failureCode));

    public Task<StoredDurableEvent> AppendCallEventAsync(
        DurableEventDraft draft,
        CallSessionState callState,
        bool terminal,
        CancellationToken cancellationToken) =>
        Task.FromException<StoredDurableEvent>(new AgentStoreException(_failureCode));

    public Task<IReadOnlyList<StoredDurableEvent>> AppendCallTransitionAsync(
        IReadOnlyList<DurableEventDraft> drafts,
        CallSessionState callState,
        bool terminal,
        CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<StoredDurableEvent>>(new AgentStoreException(_failureCode));

    public Task<IReadOnlyList<StoredDurableEvent>> LoadPendingAsync(
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<StoredDurableEvent>>(new AgentStoreException(_failureCode));

    public Task<long> CountPendingAsync(CancellationToken cancellationToken) =>
        Task.FromException<long>(new AgentStoreException(_failureCode));

    public Task<EventStoreHealth> GetHealthAsync(CancellationToken cancellationToken) =>
        Task.FromException<EventStoreHealth>(new AgentStoreException(_failureCode));

    public Task<CallSessionState?> LoadActiveCallAsync(CancellationToken cancellationToken) =>
        Task.FromException<CallSessionState?>(new AgentStoreException(_failureCode));

    public Task AcknowledgeThroughAsync(long sequence, CancellationToken cancellationToken) =>
        Task.FromException(new AgentStoreException(_failureCode));

    public Task<ProcessedCommand?> FindCommandAsync(string commandId, CancellationToken cancellationToken) =>
        Task.FromException<ProcessedCommand?>(new AgentStoreException(_failureCode));

    public Task<bool> HasCommandRequestHashPrefixAsync(string prefix, CancellationToken cancellationToken) =>
        Task.FromException<bool>(new AgentStoreException(_failureCode));

    public Task SaveCommandAsync(ProcessedCommand command, CancellationToken cancellationToken) =>
        Task.FromException(new AgentStoreException(_failureCode));

    public Task RecoverExecutingCommandsAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken) =>
        Task.FromException(new AgentStoreException(_failureCode));

    public Task PruneCommandsAsync(
        DateTimeOffset olderThanUtc,
        int maximumRetained,
        CancellationToken cancellationToken) => Task.FromException(new AgentStoreException(_failureCode));

    public Task CheckpointAsync(CancellationToken cancellationToken) =>
        Task.FromException(new AgentStoreException(_failureCode));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
