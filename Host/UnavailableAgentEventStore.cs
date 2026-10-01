using System.Collections.Concurrent;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class UnavailableAgentEventStore : IAgentEventStore
{
    private readonly ConcurrentDictionary<string, ProcessedCommand> _commands = new(StringComparer.Ordinal);

    public string AgentInstanceId { get; } = ProtocolCodec.NewId();
    public long LastSequence => 0;
    public long LastAcknowledgedSequence => 0;

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<StoredDurableEvent> AppendAsync(DurableEventDraft draft, CancellationToken cancellationToken) =>
        Task.FromException<StoredDurableEvent>(new InvalidOperationException("outbox_unavailable"));

    public Task<IReadOnlyList<StoredDurableEvent>> LoadPendingAsync(
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredDurableEvent>>([]);

    public Task<long> CountPendingAsync(CancellationToken cancellationToken) => Task.FromResult(0L);

    public Task AcknowledgeThroughAsync(long sequence, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("outbox_unavailable"));

    public Task<ProcessedCommand?> FindCommandAsync(string commandId, CancellationToken cancellationToken)
    {
        _commands.TryGetValue(commandId, out ProcessedCommand? command);
        return Task.FromResult(command);
    }

    public Task SaveCommandAsync(ProcessedCommand command, CancellationToken cancellationToken)
    {
        _commands[command.CommandId] = command;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
