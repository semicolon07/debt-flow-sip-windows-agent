using System.Net.WebSockets;
using System.Threading.Channels;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed record WebSocketOperationalHealth(
    long SessionGeneration,
    bool Connected,
    int ControlQueueDepth,
    int DurableQueueDepth,
    int RealtimeQueueDepth,
    int ControlQueueHighWater,
    int DurableQueueHighWater,
    int RealtimeQueueHighWater,
    long DroppedRealtimeEvents,
    long QueueAbortCount,
    long WriterFaultCount);

public sealed class WebSocketEventPublisher : IAgentEventPublisher, IAsyncDisposable
{
    private Session? _session;
    private long _sessionGeneration;
    private long _droppedRealtimeEvents;
    private long _queueAbortCount;
    private long _writerFaultCount;
    private SessionQueueHealth _lastQueueHealth = SessionQueueHealth.Empty;

    public bool IsConnected => Volatile.Read(ref _session) is { IsHealthy: true };

    public bool TryAttach(WebSocket socket, CancellationToken applicationToken)
    {
        var session = new Session(
            Interlocked.Increment(ref _sessionGeneration),
            socket,
            applicationToken,
            () => Interlocked.Increment(ref _writerFaultCount));
        if (Interlocked.CompareExchange(ref _session, session, null) == null)
        {
            session.Start();
            return true;
        }

        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return false;
    }

    public async Task DetachAsync(WebSocket socket)
    {
        Session? current = Volatile.Read(ref _session);
        if (current == null || !ReferenceEquals(current.Socket, socket))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _session, null, current) == current)
        {
            await current.DisposeAsync();
            Volatile.Write(ref _lastQueueHealth, current.GetQueueHealth());
        }
    }

    public Task PublishDurableAsync(StoredDurableEvent storedEvent, CancellationToken cancellationToken) =>
        SendAsync(
            ProtocolCodec.Serialize("event", storedEvent.EventType, storedEvent.ToPayload()),
            cancellationToken,
            deferUntilActive: true,
            durableSequence: storedEvent.Sequence,
            lane: OutboundLane.Durable);

    public Task PublishRealtimeAsync(
        string eventType,
        RealtimeEventPayload payload,
        CancellationToken cancellationToken) =>
        SendAsync(
            ProtocolCodec.Serialize("event", eventType, payload),
            cancellationToken,
            deferUntilActive: true,
            lane: OutboundLane.Realtime);

    public Task SendReplayAsync(StoredDurableEvent storedEvent, CancellationToken cancellationToken) =>
        SendAsync(
            ProtocolCodec.Serialize("event", storedEvent.EventType, storedEvent.ToPayload()),
            cancellationToken,
            waitForDelivery: true,
            lane: OutboundLane.Durable);

    public async Task SendReplayPageAsync(
        IReadOnlyList<StoredDurableEvent> storedEvents,
        CancellationToken cancellationToken)
    {
        if (storedEvents.Count == 0)
        {
            return;
        }

        Session? session = Volatile.Read(ref _session);
        if (session == null)
        {
            throw new WebSocketException("socket_not_open");
        }

        var deliveryBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (int index = 0; index < storedEvents.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredDurableEvent storedEvent = storedEvents[index];
            var message = new OutboundMessage(
                ProtocolCodec.Serialize("event", storedEvent.EventType, storedEvent.ToPayload()),
                index == storedEvents.Count - 1 ? deliveryBarrier : null,
                null,
                OutboundLane.Durable);
            await session.EnqueueReplayAsync(message, cancellationToken);
        }

        await deliveryBarrier.Task.WaitAsync(cancellationToken);
        if (!ReferenceEquals(session, Volatile.Read(ref _session)) || !session.IsHealthy)
        {
            throw new WebSocketException("socket_not_open");
        }
    }

    public Task SendControlAsync<T>(string kind, string type, T payload, CancellationToken cancellationToken) =>
        SendAsync(ProtocolCodec.Serialize(kind, type, payload), cancellationToken);

    public Task SendControlAndWaitAsync<T>(string kind, string type, T payload, CancellationToken cancellationToken) =>
        SendAsync(ProtocolCodec.Serialize(kind, type, payload), cancellationToken, waitForDelivery: true);

    public Task SendRawAsync(byte[] message, CancellationToken cancellationToken) => SendAsync(message, cancellationToken);

    public void Activate(WebSocket socket, long replayedThroughSequence)
    {
        Session? current = Volatile.Read(ref _session);
        if (current != null && ReferenceEquals(current.Socket, socket))
        {
            current.Activate(replayedThroughSequence);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Session? current = Interlocked.Exchange(ref _session, null);
        if (current != null)
        {
            await current.DisposeAsync();
            Volatile.Write(ref _lastQueueHealth, current.GetQueueHealth());
        }
    }

    public WebSocketOperationalHealth GetOperationalHealth()
    {
        Session? session = Volatile.Read(ref _session);
        SessionQueueHealth queue = session?.GetQueueHealth() ??
            Volatile.Read(ref _lastQueueHealth) with
            {
                ControlDepth = 0,
                DurableDepth = 0,
                RealtimeDepth = 0
            };
        return new WebSocketOperationalHealth(
            session?.Generation ?? Volatile.Read(ref _sessionGeneration),
            session is { IsHealthy: true },
            queue.ControlDepth,
            queue.DurableDepth,
            queue.RealtimeDepth,
            queue.ControlHighWater,
            queue.DurableHighWater,
            queue.RealtimeHighWater,
            Volatile.Read(ref _droppedRealtimeEvents),
            Volatile.Read(ref _queueAbortCount),
            Volatile.Read(ref _writerFaultCount));
    }

    private Task SendAsync(
        byte[] message,
        CancellationToken cancellationToken,
        bool waitForDelivery = false,
        bool deferUntilActive = false,
        long? durableSequence = null,
        OutboundLane lane = OutboundLane.Control)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Session? session = Volatile.Read(ref _session);
        if (session == null)
        {
            return Task.CompletedTask;
        }

        var outbound = new OutboundMessage(
            message,
            waitForDelivery ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null,
            durableSequence,
            lane);
        if (!session.TryEnqueue(outbound, deferUntilActive))
        {
            if (!session.IsHealthy)
            {
                return lane == OutboundLane.Realtime
                    ? Task.CompletedTask
                    : Task.FromException(new WebSocketException("socket_not_open"));
            }

            AgentPerformanceTelemetry.RecordWebSocketQueueOverflow(lane.ToString().ToLowerInvariant());
            if (lane == OutboundLane.Realtime)
            {
                AgentPerformanceTelemetry.RecordDroppedRealtimeEvent();
                Interlocked.Increment(ref _droppedRealtimeEvents);
                outbound.Completion?.TrySetResult();
                return Task.CompletedTask;
            }

            Interlocked.Increment(ref _queueAbortCount);
            session.Abort();
            return Task.FromException(new WebSocketException("outbound_queue_full"));
        }

        return outbound.Completion?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    private enum OutboundLane
    {
        Control,
        Durable,
        Realtime
    }

    private sealed record OutboundMessage(
        byte[] Payload,
        TaskCompletionSource? Completion,
        long? DurableSequence,
        OutboundLane Lane);

    private sealed record SessionQueueHealth(
        int ControlDepth,
        int DurableDepth,
        int RealtimeDepth,
        int ControlHighWater,
        int DurableHighWater,
        int RealtimeHighWater)
    {
        public static SessionQueueHealth Empty { get; } = new(0, 0, 0, 0, 0, 0);
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly object _stateGate = new();
        private readonly Queue<OutboundMessage> _deferred = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly Channel<OutboundMessage> _control;
        private readonly Channel<OutboundMessage> _activation;
        private readonly Channel<OutboundMessage> _durable;
        private readonly Channel<OutboundMessage> _realtime;
        private readonly Action _onWriterFault;
        private bool _active;
        private Task? _writerTask;
        private int _healthy = 1;
        private int _controlHighWater;
        private int _durableHighWater;
        private int _realtimeHighWater;

        public Session(
            long generation,
            WebSocket socket,
            CancellationToken applicationToken,
            Action onWriterFault)
        {
            Generation = generation;
            Socket = socket;
            _onWriterFault = onWriterFault;
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
            _control = CreateLane(64);
            _activation = CreateLane(512);
            _durable = CreateLane(256);
            _realtime = CreateLane(128);
        }

        private static Channel<OutboundMessage> CreateLane(int capacity) =>
            Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

        public WebSocket Socket { get; }
        public long Generation { get; }
        public bool IsHealthy => Volatile.Read(ref _healthy) != 0;

        public SessionQueueHealth GetQueueHealth() => new(
            _control.Reader.Count,
            _durable.Reader.Count,
            _realtime.Reader.Count,
            Volatile.Read(ref _controlHighWater),
            Volatile.Read(ref _durableHighWater),
            Volatile.Read(ref _realtimeHighWater));

        public void Start() => _writerTask = WriteLoopAsync();

        public bool TryEnqueue(OutboundMessage message, bool deferUntilActive)
        {
            lock (_stateGate)
            {
                if (!IsHealthy)
                {
                    return false;
                }

                if (deferUntilActive && !_active)
                {
                    if (_deferred.Count >= 512)
                    {
                        return false;
                    }

                    _deferred.Enqueue(message);
                    return true;
                }

                return TryWrite(message);
            }
        }

        public async ValueTask EnqueueReplayAsync(OutboundMessage message, CancellationToken cancellationToken)
        {
            if (!IsHealthy)
            {
                throw new WebSocketException("socket_not_open");
            }

            await _durable.Writer.WriteAsync(message, cancellationToken);
            _available.Release();
            RecordQueueDepth(OutboundLane.Durable);
        }

        public void Activate(long replayedThroughSequence)
        {
            bool overflow = false;
            lock (_stateGate)
            {
                if (_active)
                {
                    return;
                }

                _active = true;
                while (_deferred.TryDequeue(out OutboundMessage? message))
                {
                    if (message.DurableSequence <= replayedThroughSequence)
                    {
                        message.Completion?.TrySetResult();
                        continue;
                    }

                    if (!_activation.Writer.TryWrite(message))
                    {
                        if (message.Lane == OutboundLane.Realtime)
                        {
                            AgentPerformanceTelemetry.RecordDroppedRealtimeEvent();
                            message.Completion?.TrySetResult();
                        }
                        else
                        {
                            message.Completion?.TrySetException(new WebSocketException("outbound_queue_full"));
                            overflow = true;
                            break;
                        }
                    }
                    else
                    {
                        _available.Release();
                    }
                }

                while (overflow && _deferred.TryDequeue(out OutboundMessage? pending))
                {
                    pending.Completion?.TrySetException(new WebSocketException("outbound_queue_full"));
                }
            }

            if (overflow)
            {
                Abort();
            }
        }

        public void Abort()
        {
            Interlocked.Exchange(ref _healthy, 0);
            _shutdown.Cancel();
            Socket.Abort();
        }

        public async ValueTask DisposeAsync()
        {
            lock (_stateGate)
            {
                while (_deferred.TryDequeue(out OutboundMessage? pending))
                {
                    pending.Completion?.TrySetCanceled();
                }
            }

            CompleteLanes();
            _shutdown.Cancel();
            if (_writerTask != null)
            {
                try
                {
                    await _writerTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (WebSocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _available.Dispose();
            _shutdown.Dispose();
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                int durableBurst = 0;
                while (!_shutdown.IsCancellationRequested)
                {
                    await _available.WaitAsync(_shutdown.Token);
                    if (!TryReadNext(ref durableBurst, out OutboundMessage? message))
                    {
                        continue;
                    }
                    OutboundMessage next = message!;

                    if (Socket.State != WebSocketState.Open)
                    {
                        next.Completion?.TrySetException(new WebSocketException("socket_not_open"));
                        AgentPerformanceTelemetry.RecordPublisherFailure("writer");
                        _onWriterFault();
                        return;
                    }

                    try
                    {
                        await Socket.SendAsync(next.Payload, WebSocketMessageType.Text, true, _shutdown.Token);
                        next.Completion?.TrySetResult();
                    }
                    catch (OperationCanceledException exception)
                    {
                        next.Completion?.TrySetCanceled(exception.CancellationToken);
                        throw;
                    }
                    catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
                    {
                        next.Completion?.TrySetException(exception);
                        AgentPerformanceTelemetry.RecordPublisherFailure("writer");
                        _onWriterFault();
                        throw;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _healthy, 0);
                CompleteLanes();
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    Socket.Abort();
                }
                while (TryReadAny(out OutboundMessage? pending))
                {
                    pending!.Completion?.TrySetCanceled();
                }
            }
        }

        private bool TryWrite(OutboundMessage message)
        {
            bool written = WriterFor(message.Lane).TryWrite(message);
            if (written)
            {
                _available.Release();
                RecordQueueDepth(message.Lane);
            }

            return written;
        }

        private void RecordQueueDepth(OutboundLane lane)
        {
            int depth = lane switch
            {
                OutboundLane.Control => _control.Reader.Count,
                OutboundLane.Durable => _durable.Reader.Count,
                _ => _realtime.Reader.Count
            };
            switch (lane)
            {
                case OutboundLane.Control:
                    UpdateHighWater(ref _controlHighWater, lane, depth);
                    break;
                case OutboundLane.Durable:
                    UpdateHighWater(ref _durableHighWater, lane, depth);
                    break;
                default:
                    UpdateHighWater(ref _realtimeHighWater, lane, depth);
                    break;
            }
        }

        private static void UpdateHighWater(ref int highWater, OutboundLane lane, int depth)
        {
            int observed = Volatile.Read(ref highWater);
            while (depth > observed)
            {
                int previous = Interlocked.CompareExchange(ref highWater, depth, observed);
                if (previous == observed)
                {
                    AgentPerformanceTelemetry.RecordQueueHighWater(
                        $"websocket_{lane.ToString().ToLowerInvariant()}",
                        depth);
                    break;
                }

                observed = previous;
            }
        }

        private void CompleteLanes()
        {
            _control.Writer.TryComplete();
            _activation.Writer.TryComplete();
            _durable.Writer.TryComplete();
            _realtime.Writer.TryComplete();
        }

        private ChannelWriter<OutboundMessage> WriterFor(OutboundLane lane) => lane switch
        {
            OutboundLane.Control => _control.Writer,
            OutboundLane.Durable => _durable.Writer,
            _ => _realtime.Writer
        };

        private bool TryReadNext(ref int durableBurst, out OutboundMessage? message)
        {
            if (_control.Reader.TryRead(out message))
            {
                return true;
            }

            if (_activation.Reader.TryRead(out message))
            {
                return true;
            }

            if (durableBurst >= 8 && _realtime.Reader.TryRead(out message))
            {
                durableBurst = 0;
                return true;
            }

            if (_durable.Reader.TryRead(out message))
            {
                durableBurst++;
                return true;
            }

            if (_realtime.Reader.TryRead(out message))
            {
                durableBurst = 0;
                return true;
            }

            return false;
        }

        private bool TryReadAny(out OutboundMessage? message) =>
            _control.Reader.TryRead(out message) ||
            _activation.Reader.TryRead(out message) ||
            _durable.Reader.TryRead(out message) ||
            _realtime.Reader.TryRead(out message);
    }
}
