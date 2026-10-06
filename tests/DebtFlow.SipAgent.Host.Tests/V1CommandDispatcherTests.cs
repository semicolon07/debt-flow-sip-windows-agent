using System.Security.Cryptography;
using System.Threading.Channels;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class V1CommandDispatcherTests
{
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
    public async Task UnsupportedCommand_IsRejectedBeforeJournalInsertion()
    {
        await using DispatcherFixture fixture = await DispatcherFixture.CreateAsync();
        string commandId = ProtocolCodec.NewId();

        byte[] response = await fixture.Dispatcher.DispatchAsync(
            Envelope("call.transfer", new CommandHeader(commandId)),
            CancellationToken.None);

        Assert.Equal("invalid_message", Result(response).ErrorCode);
        Assert.Null(await fixture.Store.FindCommandAsync(commandId, CancellationToken.None));
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
        public ValueTask DisposeAsync()
        {
            _signals.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
