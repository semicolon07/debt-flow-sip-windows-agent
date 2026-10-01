using System.Net.WebSockets;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class WebSocketEventPublisher : IAgentEventPublisher
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private WebSocket? _socket;

    public bool TryAttach(WebSocket socket) => Interlocked.CompareExchange(ref _socket, socket, null) == null;

    public void Detach(WebSocket socket)
    {
        Interlocked.CompareExchange(ref _socket, null, socket);
    }

    public Task PublishDurableAsync(StoredDurableEvent storedEvent, CancellationToken cancellationToken) =>
        SendAsync(
            ProtocolCodec.Serialize("event", storedEvent.EventType, storedEvent.ToPayload()),
            cancellationToken);

    public Task PublishRealtimeAsync(
        string eventType,
        RealtimeEventPayload payload,
        CancellationToken cancellationToken) =>
        SendAsync(ProtocolCodec.Serialize("event", eventType, payload), cancellationToken);

    public Task SendControlAsync<T>(
        string kind,
        string type,
        T payload,
        CancellationToken cancellationToken) =>
        SendAsync(ProtocolCodec.Serialize(kind, type, payload), cancellationToken);

    public async Task SendRawAsync(byte[] message, CancellationToken cancellationToken) =>
        await SendAsync(message, cancellationToken);

    private async Task SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        WebSocket? socket = Volatile.Read(ref _socket);
        if (socket is not { State: WebSocketState.Open })
        {
            return;
        }

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        catch (WebSocketException)
        {
            // The receive loop owns disconnect handling. Durable events remain in the outbox.
        }
        finally
        {
            _sendGate.Release();
        }
    }
}
