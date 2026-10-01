using System.Net.WebSockets;
using System.Threading.Channels;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class WebSocketEventPublisher : IAgentEventPublisher, IAsyncDisposable
{
    private const int OutboundCapacity = 256;
    private Session? _session;

    public bool IsConnected => Volatile.Read(ref _session) != null;

    public bool TryAttach(WebSocket socket, CancellationToken applicationToken)
    {
        var session = new Session(socket, applicationToken);
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
        }
    }

    public Task PublishDurableAsync(StoredDurableEvent storedEvent, CancellationToken cancellationToken) =>
        SendAsync(
            ProtocolCodec.Serialize("event", storedEvent.EventType, storedEvent.ToPayload()),
            cancellationToken,
            deferUntilActive: true,
            durableSequence: storedEvent.Sequence);

    public Task PublishRealtimeAsync(
        string eventType,
        RealtimeEventPayload payload,
        CancellationToken cancellationToken) =>
        SendAsync(
            ProtocolCodec.Serialize("event", eventType, payload),
            cancellationToken,
            deferUntilActive: true);

    public Task SendReplayAsync(StoredDurableEvent storedEvent, CancellationToken cancellationToken) =>
        SendAsync(
            ProtocolCodec.Serialize("event", storedEvent.EventType, storedEvent.ToPayload()),
            cancellationToken,
            waitForDelivery: true);

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
        }
    }

    private Task SendAsync(
        byte[] message,
        CancellationToken cancellationToken,
        bool waitForDelivery = false,
        bool deferUntilActive = false,
        long? durableSequence = null)
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
            durableSequence);
        if (!session.TryEnqueue(outbound, deferUntilActive))
        {
            session.Abort();
            return waitForDelivery
                ? Task.FromException(new WebSocketException("outbound_queue_full"))
                : Task.CompletedTask;
        }

        return outbound.Completion?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    private sealed record OutboundMessage(byte[] Payload, TaskCompletionSource? Completion, long? DurableSequence);

    private sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly object _stateGate = new();
        private readonly Queue<OutboundMessage> _deferred = new();
        private bool _active;
        private Task? _writerTask;

        public Session(WebSocket socket, CancellationToken applicationToken)
        {
            Socket = socket;
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
            Outbound = Channel.CreateBounded<OutboundMessage>(new BoundedChannelOptions(OutboundCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        }

        public WebSocket Socket { get; }
        public Channel<OutboundMessage> Outbound { get; }

        public void Start() => _writerTask = WriteLoopAsync();

        public bool TryEnqueue(OutboundMessage message, bool deferUntilActive)
        {
            lock (_stateGate)
            {
                if (deferUntilActive && !_active)
                {
                    if (_deferred.Count >= OutboundCapacity)
                    {
                        return false;
                    }

                    _deferred.Enqueue(message);
                    return true;
                }

                return Outbound.Writer.TryWrite(message);
            }
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

                    if (!Outbound.Writer.TryWrite(message))
                    {
                        message.Completion?.TrySetException(new WebSocketException("outbound_queue_full"));
                        overflow = true;
                        break;
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

            Outbound.Writer.TryComplete();
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

            _shutdown.Dispose();
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (OutboundMessage message in Outbound.Reader.ReadAllAsync(_shutdown.Token))
                {
                    if (Socket.State != WebSocketState.Open)
                    {
                        message.Completion?.TrySetException(new WebSocketException("socket_not_open"));
                        return;
                    }

                    try
                    {
                        await Socket.SendAsync(message.Payload, WebSocketMessageType.Text, true, _shutdown.Token);
                        message.Completion?.TrySetResult();
                    }
                    catch (OperationCanceledException exception)
                    {
                        message.Completion?.TrySetCanceled(exception.CancellationToken);
                        throw;
                    }
                    catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
                    {
                        message.Completion?.TrySetException(exception);
                        throw;
                    }
                }
            }
            finally
            {
                while (Outbound.Reader.TryRead(out OutboundMessage? pending))
                {
                    pending.Completion?.TrySetCanceled();
                }
            }
        }
    }
}
