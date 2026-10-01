using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Host;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class WebSocketEventPublisherTests
{
    [Fact]
    public async Task Replay_WaitsForSocketDeliveryToApplyBackpressure()
    {
        await using var publisher = new WebSocketEventPublisher();
        var sendRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var socket = new RecordingWebSocket(sendRelease.Task);
        Assert.True(publisher.TryAttach(socket, CancellationToken.None));

        Task replay = publisher.SendReplayAsync(StoredEvent(1), CancellationToken.None);

        Assert.False(replay.IsCompleted);
        sendRelease.SetResult();
        await replay.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(socket.Messages);
    }

    [Fact]
    public async Task HandshakeBuffer_SendsWelcomeSnapshotReplayBeforeLiveEventsAndFiltersReplayDuplicate()
    {
        await using var publisher = new WebSocketEventPublisher();
        using var socket = new RecordingWebSocket();
        Assert.True(publisher.TryAttach(socket, CancellationToken.None));

        await publisher.PublishRealtimeAsync(
            "registration.state_changed",
            new RealtimeEventPayload(
                "realtime",
                NewId(),
                NewId(),
                NewId(),
                DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { state = "registered" })),
            CancellationToken.None);
        StoredDurableEvent sequenceOne = StoredEvent(1);
        StoredDurableEvent sequenceTwo = StoredEvent(2);
        await publisher.PublishDurableAsync(sequenceOne, CancellationToken.None);

        await publisher.SendControlAsync("welcome", "session.welcome", new { }, CancellationToken.None);
        await publisher.SendControlAsync("snapshot", "agent.snapshot", new { }, CancellationToken.None);
        await publisher.SendReplayAsync(sequenceOne, CancellationToken.None);
        await publisher.PublishDurableAsync(sequenceTwo, CancellationToken.None);

        publisher.Activate(socket, replayedThroughSequence: 1);
        await socket.WaitForMessageCountAsync(5);

        ProtocolEnvelope[] messages = socket.Messages.Select(message => ProtocolCodec.Deserialize(message)).ToArray();
        Assert.Equal(
            ["session.welcome", "agent.snapshot", "call.created", "registration.state_changed", "call.created"],
            messages.Select(message => message.Type));
        long[] durableSequences = messages
            .Where(message => message.Kind == "event" && message.Type == "call.created")
            .Select(message => ProtocolCodec.DeserializePayload<DurableEventPayload>(message.Payload).Sequence)
            .ToArray();
        Assert.Equal([1, 2], durableSequences);
    }

    private static StoredDurableEvent StoredEvent(long sequence) => new(
        sequence,
        NewId(),
        NewId(),
        NewId(),
        NewId(),
        null,
        "call.created",
        DateTimeOffset.UtcNow,
        "created",
        "{}");

    private static string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();

    private sealed class RecordingWebSocket : WebSocket
    {
        private readonly ConcurrentQueue<byte[]> _messages = new();
        private readonly Task _sendRelease;
        private WebSocketState _state = WebSocketState.Open;

        public RecordingWebSocket(Task? sendRelease = null)
        {
            _sendRelease = sendRelease ?? Task.CompletedTask;
        }

        public IReadOnlyList<byte[]> Messages => _messages.ToArray();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            await _sendRelease.WaitAsync(cancellationToken);
            _messages.Enqueue(buffer.ToArray());
        }

        public async Task WaitForMessageCountAsync(int expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (_messages.Count < expected)
            {
                await Task.Delay(5, timeout.Token);
            }
        }
    }
}
