using System.Security.Cryptography;
using System.Threading.Channels;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class V1CommandDispatcherTests
{
    [Fact]
    public async Task ConfigureHostname_CanonicalizesWithoutChangingV1CommandShape()
    {
        await using DispatcherFixture fixture = await DispatcherFixture.CreateAsync();
        string commandId = ProtocolCodec.NewId();

        byte[] first = await fixture.Dispatcher.DispatchAsync(
            Envelope(
                "session.configure",
                new ConfigureCommand(commandId, "PBX.Branch.Example.", 5060, "100", "secret")),
            CancellationToken.None);
        byte[] replay = await fixture.Dispatcher.DispatchAsync(
            Envelope(
                "session.configure",
                new ConfigureCommand(commandId, "pbx.branch.example", 5060, "100", "secret")),
            CancellationToken.None);

        Assert.True(Result(first).Accepted);
        Assert.True(Result(replay).Accepted);
        Assert.NotEqual("command_duplicate_conflict", Result(replay).ErrorCode);
    }

    [Fact]
    public async Task SameCommandId_WithDifferentCanonicalPayload_ReturnsConflict()
    {
        await using DispatcherFixture fixture = await DispatcherFixture.CreateAsync();
        string commandId = ProtocolCodec.NewId();
        string callId = ProtocolCodec.NewId();

        byte[] first = await fixture.Dispatcher.DispatchAsync(
            Envelope("call.mute.set", new CallMuteCommand(commandId, callId, true)),
            CancellationToken.None);
        byte[] conflicting = await fixture.Dispatcher.DispatchAsync(
            Envelope("call.mute.set", new CallMuteCommand(commandId, callId, false)),
            CancellationToken.None);

        CommandResultPayload firstResult = Result(first);
        CommandResultPayload conflictResult = Result(conflicting);
        Assert.False(firstResult.Accepted);
        Assert.False(conflictResult.Accepted);
        Assert.Equal("command_duplicate_conflict", conflictResult.ErrorCode);
        Assert.StartsWith(
            "v2:",
            (await fixture.Store.FindCommandAsync(commandId, CancellationToken.None))!.RequestHash,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AudioPreferenceCommand_DoesNotRequireCallId()
    {
        await using DispatcherFixture fixture = await DispatcherFixture.CreateAsync();
        string commandId = ProtocolCodec.NewId();

        byte[] response = await fixture.Dispatcher.DispatchAsync(
            Envelope(
                "audio.output.volume.preference.set",
                new AudioVolumePreferenceCommand(commandId, 70)),
            CancellationToken.None);

        Assert.True(Result(response).Accepted);
    }

    [Fact]
    public async Task AudioDeviceCommands_DoNotRequireCallId()
    {
        await using DispatcherFixture fixture = await DispatcherFixture.CreateAsync();

        byte[] preferenceResponse = await fixture.Dispatcher.DispatchAsync(
            Envelope(
                "audio.devices.preference.set",
                new AudioDevicePreferenceCommand(ProtocolCodec.NewId(), "system-default", "system-default")),
            CancellationToken.None);
        byte[] outputTestResponse = await fixture.Dispatcher.DispatchAsync(
            Envelope(
                "audio.output.test",
                new AudioDeviceTestCommand(ProtocolCodec.NewId(), "system-default")),
            CancellationToken.None);
        byte[] inputTestResponse = await fixture.Dispatcher.DispatchAsync(
            Envelope(
                "audio.input.test",
                new AudioDeviceTestCommand(ProtocolCodec.NewId(), "system-default")),
            CancellationToken.None);

        Assert.True(Result(preferenceResponse).Accepted);
        Assert.True(Result(outputTestResponse).Accepted);
        Assert.True(Result(inputTestResponse).Accepted);
    }

    [Fact]
    public async Task UnsupportedCommand_IsRejectedBeforeJournalInsertion()
    {
        await using DispatcherFixture fixture = await DispatcherFixture.CreateAsync();
        string commandId = ProtocolCodec.NewId();
        ProtocolEnvelope command = Envelope("call.transfer", new CommandHeader(commandId));

        byte[] response = await fixture.Dispatcher.DispatchAsync(command, CancellationToken.None);
        ProtocolEnvelope envelope = ProtocolCodec.Deserialize(response);
        ErrorPayload error = ProtocolCodec.DeserializePayload<ErrorPayload>(envelope.Payload);

        Assert.Equal("error", envelope.Kind);
        Assert.Equal("protocol.error", envelope.Type);
        Assert.Equal(command.MessageId, error.CorrelationMessageId);
        Assert.Equal("invalid_message", error.Code);
        Assert.Null(await fixture.Store.FindCommandAsync(commandId, CancellationToken.None));
    }

    [Fact]
    public async Task RequestCancellationAfterExecutingMarker_DoesNotStrandCommandJournal()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-dispatcher-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
        await store.InitializeAsync(CancellationToken.None);
        using var request = new CancellationTokenSource();
        var cancelingStore = new CancelAfterExecutingStore(store, request);
        var coordinator = new AgentCoordinator(
            new FakeSipRuntime(),
            cancelingStore,
            new NullPublisher(),
            new SystemAgentClock(),
            new GuidAgentIdGenerator());
        await coordinator.InitializeAsync(CancellationToken.None);
        var dispatcher = new V1CommandDispatcher(
            coordinator,
            cancelingStore,
            new SystemAgentClock(),
            new HashFingerprintService());
        string commandId = ProtocolCodec.NewId();
        try
        {
            byte[] response = await dispatcher.DispatchAsync(
                Envelope("state.get", new CommandHeader(commandId)),
                request.Token);

            Assert.True(Result(response).Accepted);
            ProcessedCommand persisted = Assert.IsType<ProcessedCommand>(
                await store.FindCommandAsync(commandId, CancellationToken.None));
            Assert.Equal("completed", persisted.ExecutionState);
        }
        finally
        {
            await dispatcher.DisposeAsync();
            await coordinator.DisposeAsync();
            await store.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProtocolEnvelope Envelope(string type, object payload) =>
        ProtocolCodec.Deserialize(ProtocolCodec.Serialize("command", type, payload));

    private static CommandResultPayload Result(byte[] bytes)
    {
        ProtocolEnvelope envelope = ProtocolCodec.Deserialize(bytes);
        return ProtocolCodec.DeserializePayload<CommandResultPayload>(envelope.Payload);
    }

    private sealed class DispatcherFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly AgentCoordinator _coordinator;

        private DispatcherFixture(
            string directory,
            SqliteAgentEventStore store,
            AgentCoordinator coordinator,
            V1CommandDispatcher dispatcher)
        {
            _directory = directory;
            Store = store;
            _coordinator = coordinator;
            Dispatcher = dispatcher;
        }

        public SqliteAgentEventStore Store { get; }
        public V1CommandDispatcher Dispatcher { get; }

        public static async Task<DispatcherFixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"sip-dispatcher-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
            await store.InitializeAsync(CancellationToken.None);
            var coordinator = new AgentCoordinator(
                new FakeSipRuntime(),
                store,
                new NullPublisher(),
                new SystemAgentClock(),
                new GuidAgentIdGenerator());
            await coordinator.InitializeAsync(CancellationToken.None);
            return new DispatcherFixture(
                directory,
                store,
                coordinator,
                new V1CommandDispatcher(
                    coordinator,
                    store,
                    new SystemAgentClock(),
                    new HashFingerprintService()));
        }

        public async ValueTask DisposeAsync()
        {
            await Dispatcher.DisposeAsync();
            await _coordinator.DisposeAsync();
            await Store.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class HashFingerprintService : ICommandFingerprintService
    {
        public CommandFingerprint Compute(ReadOnlySpan<byte> canonicalPayload) => new(
            $"v2:{Convert.ToHexString(SHA256.HashData(canonicalPayload)).ToLowerInvariant()}",
            false);
    }

    private sealed class CancelAfterExecutingStore(
        IAgentEventStore inner,
        CancellationTokenSource request) : IAgentEventStore
    {
        public string AgentInstanceId => inner.AgentInstanceId;
        public long LastSequence => inner.LastSequence;
        public long LastAcknowledgedSequence => inner.LastAcknowledgedSequence;
        public Task InitializeAsync(CancellationToken token) => inner.InitializeAsync(token);
        public Task<StoredDurableEvent> AppendAsync(DurableEventDraft draft, CancellationToken token) =>
            inner.AppendAsync(draft, token);
        public Task<StoredDurableEvent> AppendCallEventAsync(
            DurableEventDraft draft,
            CallSessionState state,
            bool terminal,
            CancellationToken token) => inner.AppendCallEventAsync(draft, state, terminal, token);
        public Task<IReadOnlyList<StoredDurableEvent>> AppendCallTransitionAsync(
            IReadOnlyList<DurableEventDraft> drafts,
            CallSessionState state,
            bool terminal,
            CancellationToken token) => inner.AppendCallTransitionAsync(drafts, state, terminal, token);
        public Task<IReadOnlyList<StoredDurableEvent>> LoadPendingAsync(long after, int count, CancellationToken token) =>
            inner.LoadPendingAsync(after, count, token);
        public Task<long> CountPendingAsync(CancellationToken token) => inner.CountPendingAsync(token);
        public Task<EventStoreHealth> GetHealthAsync(CancellationToken token) => inner.GetHealthAsync(token);
        public Task<CallSessionState?> LoadActiveCallAsync(CancellationToken token) => inner.LoadActiveCallAsync(token);
        public Task AcknowledgeThroughAsync(long sequence, CancellationToken token) =>
            inner.AcknowledgeThroughAsync(sequence, token);
        public Task<ProcessedCommand?> FindCommandAsync(string commandId, CancellationToken token) =>
            inner.FindCommandAsync(commandId, token);
        public Task<bool> HasCommandRequestHashPrefixAsync(string prefix, CancellationToken token) =>
            inner.HasCommandRequestHashPrefixAsync(prefix, token);

        public async Task SaveCommandAsync(ProcessedCommand command, CancellationToken token)
        {
            await inner.SaveCommandAsync(command, token);
            if (command.ExecutionState == "executing")
            {
                request.Cancel();
            }
        }

        public Task RecoverExecutingCommandsAsync(DateTimeOffset recoveredAtUtc, CancellationToken token) =>
            inner.RecoverExecutingCommandsAsync(recoveredAtUtc, token);
        public Task PruneCommandsAsync(DateTimeOffset olderThanUtc, int maximumRetained, CancellationToken token) =>
            inner.PruneCommandsAsync(olderThanUtc, maximumRetained, token);
        public Task CheckpointAsync(CancellationToken token) => inner.CheckpointAsync(token);
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

    private sealed class FakeSipRuntime : ISipRuntime
    {
        private readonly Channel<SipSignal> _signals = Channel.CreateUnbounded<SipSignal>();
        public ChannelReader<SipSignal> Signals => _signals.Reader;
        public string AudioState => "ready";
        public bool IsMicrophoneMuted => false;
        public int OutputVolume => 100;
        public int InputVolume => 100;
        public AudioDevicesSnapshot AudioDevices => new(
            [new AudioDeviceSnapshot("system-default", "System default", true)],
            [new AudioDeviceSnapshot("system-default", "System default", true)],
            "system-default",
            "system-default");
        public Task ConfigureAsync(SipConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartRegistrationAsync(long generation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopRegistrationAsync(long generation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartCallAsync(SipCallHandle call, string destination, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AnswerAsync(SipCallHandle call, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RejectAsync(SipCallHandle call, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RejectUnavailableAsync(SipCallHandle call, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task HangupAsync(SipCallHandle call, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendDtmfAsync(SipCallHandle call, char digit, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetMicrophoneMutedAsync(SipCallHandle call, bool muted, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetOutputVolumeAsync(SipCallHandle call, int volume, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetInputVolumeAsync(SipCallHandle call, int volume, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetOutputVolumePreferenceAsync(int volume, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetInputVolumePreferenceAsync(int volume, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetAudioDevicePreferencesAsync(string outputDeviceId, string inputDeviceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TestOutputDeviceAsync(string deviceId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> TestInputDeviceAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult(42);
        public ValueTask DisposeAsync()
        {
            _signals.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
