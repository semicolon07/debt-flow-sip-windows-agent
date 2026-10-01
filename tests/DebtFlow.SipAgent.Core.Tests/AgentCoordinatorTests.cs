using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Core.Tests;

public sealed class AgentCoordinatorTests
{
    [Fact]
    public async Task StartCall_BeforeRegistration_IsRejectedWithoutDurableEvent()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        var command = new CallStartCommand(NewId(), NewId(), "0812345678", "diagnostic-only");

        AgentCommandException exception = await Assert.ThrowsAsync<AgentCommandException>(
            () => fixture.Coordinator.StartCallAsync(command, CancellationToken.None));

        Assert.Equal("registration_unavailable", exception.ErrorCode);
        Assert.Equal(0, await fixture.Store.CountPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateTerminalSignal_CreatesOneCallEndedEvent()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));
        await fixture.Coordinator.HangupAsync(new CallCommand(NewId(), callId), CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallRemoteEnded));

        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        StoredDurableEvent ended = Assert.Single(events, item => item.EventType == "call.ended");
        Assert.Contains("completed", ended.DataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("0812345678", string.Join(string.Empty, events.Select(item => item.DataJson)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DtmfEvent_DoesNotPersistDigit()
    {
        await using CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync();
        await fixture.RegisterAsync();
        string callId = NewId();
        await fixture.Coordinator.StartCallAsync(
            new CallStartCommand(NewId(), callId, "0812345678", "diagnostic-only"),
            CancellationToken.None);
        await fixture.Runtime.EmitAsync(new SipSignal(SipSignalType.CallConnected, 200));

        await fixture.Coordinator.SendDtmfAsync(
            new DtmfCommand(NewId(), callId, "9"),
            CancellationToken.None);

        IReadOnlyList<StoredDurableEvent> events = await fixture.Store.LoadPendingAsync(0, 100, CancellationToken.None);
        StoredDurableEvent dtmf = events.Single(item => item.EventType == "call.dtmf_sent");
        Assert.DoesNotContain("9", dtmf.DataJson, StringComparison.Ordinal);
        Assert.Equal('9', fixture.Runtime.LastDtmf);
    }

    private static string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    private sealed class CoordinatorFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private CoordinatorFixture(
            string directory,
            SqliteAgentEventStore store,
            FakeSipRuntime runtime,
            AgentCoordinator coordinator)
        {
            _directory = directory;
            Store = store;
            Runtime = runtime;
            Coordinator = coordinator;
        }

        public SqliteAgentEventStore Store { get; }
        public FakeSipRuntime Runtime { get; }
        public AgentCoordinator Coordinator { get; }

        public static async Task<CoordinatorFixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"sip-agent-coordinator-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
            await store.InitializeAsync(CancellationToken.None);
            var runtime = new FakeSipRuntime();
            var coordinator = new AgentCoordinator(
                runtime,
                store,
                new NullPublisher(),
                new FixedClock(),
                new GuidAgentIdGenerator());
            return new CoordinatorFixture(directory, store, runtime, coordinator);
        }

        public async Task RegisterAsync()
        {
            await Coordinator.ConfigureAsync(
                new ConfigureCommand(NewId(), "192.0.2.10", 5060, "user", "password"),
                CancellationToken.None);
            await Coordinator.StartRegistrationAsync(CancellationToken.None);
            await Runtime.EmitAsync(new SipSignal(SipSignalType.RegistrationRegistered));
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Store.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class FakeSipRuntime : ISipRuntime
    {
        public event Func<SipSignal, Task>? Signal;
        public string AudioState => "ready";
        public char? LastDtmf { get; private set; }

        public Task EmitAsync(SipSignal signal) => Signal?.Invoke(signal) ?? Task.CompletedTask;
        public Task ConfigureAsync(SipConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartRegistrationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopRegistrationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartCallAsync(string destination, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AnswerAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RejectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HangupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendDtmfAsync(char digit, CancellationToken cancellationToken)
        {
            LastDtmf = digit;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullPublisher : IAgentEventPublisher
    {
        public Task PublishDurableAsync(StoredDurableEvent storedEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PublishRealtimeAsync(
            string eventType,
            RealtimeEventPayload payload,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedClock : IAgentClock
    {
        private long _ticks;
        public DateTimeOffset UtcNow =>
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(Interlocked.Increment(ref _ticks));
    }
}
