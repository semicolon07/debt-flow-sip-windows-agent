using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class LocalWebSocketServer(
    AgentCoordinator coordinator,
    IAgentEventStore eventStore,
    WebSocketEventPublisher publisher,
    V1CommandDispatcher dispatcher,
    ILogger<LocalWebSocketServer> logger,
    AgentRuntimeOptions options)
{
    private int _clientConnected;

    public bool IsClientConnected => Volatile.Read(ref _clientConnected) != 0;

    public async Task HandleAsync(HttpContext context)
    {
        if (!options.IsOperational)
        {
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            return;
        }

        if (context.Request.QueryString.HasValue)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        string? origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!OriginPolicy.IsAllowed(origin, options.AllowedOrigins))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        if (Interlocked.CompareExchange(ref _clientConnected, 1, 0) != 0)
        {
            using WebSocket rejected = await context.WebSockets.AcceptWebSocketAsync();
            await SendDirectAsync(
                rejected,
                ProtocolCodec.Serialize(
                    "error",
                    "protocol.error",
                    new ErrorPayload(null, "client_already_connected", false)),
                context.RequestAborted);
            await rejected.CloseAsync(WebSocketCloseStatus.PolicyViolation, "client_already_connected", context.RequestAborted);
            return;
        }

        WebSocket? socket = null;
        bool portalConnected = false;
        bool publisherAttached = false;
        string? correlationMessageId = null;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync();
            var rateWindow = new Queue<DateTimeOffset>();
            ProtocolEnvelope hello = await ReceiveAsync(socket, rateWindow, context.RequestAborted);
            correlationMessageId = hello.MessageId;
            ValidateHello(hello);
            HelloPayload helloPayload = ProtocolCodec.DeserializePayload<HelloPayload>(hello.Payload);
            ValidateHelloPayload(helloPayload);
            if (!helloPayload.SupportedProtocolVersions.Contains(ProtocolConstants.Version))
            {
                await SendDirectAsync(
                    socket,
                    ProtocolCodec.Serialize(
                        "error",
                        "protocol.error",
                        new ErrorPayload(hello.MessageId, "protocol_version_unsupported", false)),
                    context.RequestAborted);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "protocol_version_unsupported", context.RequestAborted);
                return;
            }

            if (!publisher.TryAttach(socket, context.RequestAborted))
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "client_already_connected", context.RequestAborted);
                return;
            }

            publisherAttached = true;
            await coordinator.PortalConnectedAsync(context.RequestAborted);
            portalConnected = true;
            await SendWelcomeAndStateAsync(context.RequestAborted);
            long replayedThroughSequence = await ReplayPendingEventsAsync(context.RequestAborted);
            publisher.Activate(socket, replayedThroughSequence);

            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                ProtocolEnvelope message = await ReceiveAsync(socket, rateWindow, context.RequestAborted);
                correlationMessageId = message.MessageId;
                if (message.Kind == "ping" && message.Type == "session.ping")
                {
                    _ = ProtocolCodec.DeserializePayload<EmptyPayload>(message.Payload);
                    await publisher.SendControlAsync(
                        "pong",
                        "session.pong",
                        new { correlationMessageId = message.MessageId },
                        context.RequestAborted);
                    continue;
                }

                byte[] response = await dispatcher.DispatchAsync(message, context.RequestAborted);
                await publisher.SendRawAsync(response, context.RequestAborted);
                if (message.Kind == "command" && message.Type == "state.get")
                {
                    await SendSnapshotAsync(context.RequestAborted);
                }
            }
        }
        catch (ProtocolException exception)
        {
            logger.LogWarning("WebSocket protocol message rejected with code {Code}", exception.Code);
            if (socket is { State: WebSocketState.Open })
            {
                if (publisherAttached)
                {
                    await publisher.SendControlAndWaitAsync(
                        "error",
                        "protocol.error",
                        new ErrorPayload(correlationMessageId, exception.Code, false),
                        CancellationToken.None);
                }
                else
                {
                    await SendDirectAsync(
                        socket,
                        ProtocolCodec.Serialize(
                            "error",
                            "protocol.error",
                            new ErrorPayload(correlationMessageId, exception.Code, false)),
                        CancellationToken.None);
                }

                await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, exception.Code, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            if (socket is { State: WebSocketState.Open })
            {
                await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "timeout", CancellationToken.None);
            }
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation("WebSocket disconnected with {ErrorType}", exception.GetType().Name);
        }
        catch (Exception exception)
        {
            logger.LogError("WebSocket session failed with {ErrorType}", exception.GetType().Name);
        }
        finally
        {
            if (socket != null)
            {
                await publisher.DetachAsync(socket);
                socket.Dispose();
            }

            if (portalConnected)
            {
                try
                {
                    await coordinator.PortalDisconnectedAsync(CancellationToken.None);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Portal disconnect cleanup failed with {ErrorType}", exception.GetType().Name);
                }
            }

            Interlocked.Exchange(ref _clientConnected, 0);
        }
    }

    private async Task SendWelcomeAndStateAsync(CancellationToken cancellationToken)
    {
        await publisher.SendControlAsync(
            "welcome",
            "session.welcome",
            new WelcomePayload(
                GetAgentVersion(),
                eventStore.AgentInstanceId,
                coordinator.AgentSessionId,
                ["sip.register", "call.outbound", "call.inbound", "call.dtmf", "event.durable", "event.collection_binding.v1"],
                eventStore.LastSequence,
                eventStore.LastAcknowledgedSequence),
            cancellationToken);
        await SendSnapshotAsync(cancellationToken);
    }

    private static string GetAgentVersion()
    {
        Version? version = typeof(LocalWebSocketServer).Assembly.GetName().Version;
        return version == null
            ? "1.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private async Task SendSnapshotAsync(CancellationToken cancellationToken)
    {
        AgentSnapshotPayload snapshot = await coordinator.GetSnapshotAsync(cancellationToken);
        await publisher.SendControlAsync("snapshot", "agent.snapshot", snapshot, cancellationToken);
    }

    private async Task<long> ReplayPendingEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            long afterSequence = eventStore.LastAcknowledgedSequence;
            while (true)
            {
                IReadOnlyList<StoredDurableEvent> page = await eventStore.LoadPendingAsync(afterSequence, 250, cancellationToken);
                if (page.Count == 0)
                {
                    return afterSequence;
                }

                foreach (StoredDurableEvent storedEvent in page)
                {
                    await publisher.SendReplayAsync(storedEvent, cancellationToken);
                    afterSequence = storedEvent.Sequence;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await coordinator.MarkDegradedAsync("outbox_unavailable", CancellationToken.None);
            throw;
        }
    }

    private static async Task<ProtocolEnvelope> ReceiveAsync(
        WebSocket socket,
        Queue<DateTimeOffset> rateWindow,
        CancellationToken applicationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        timeout.CancelAfter(ProtocolConstants.ClientTimeout);
        byte[] chunk = new byte[8 * 1024];
        using var buffer = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new OperationCanceledException("Client closed the WebSocket.");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new ProtocolException("invalid_message", "Only text messages are supported.");
            }

            if (buffer.Length + result.Count > ProtocolConstants.MaximumMessageBytes)
            {
                throw new ProtocolException("message_too_large", "Message exceeds the maximum size.");
            }

            buffer.Write(chunk, 0, result.Count);
        }
        while (!result.EndOfMessage);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        while (rateWindow.TryPeek(out DateTimeOffset oldest) && now - oldest >= ProtocolConstants.MessageRateWindow)
        {
            rateWindow.Dequeue();
        }

        if (rateWindow.Count >= ProtocolConstants.MaximumMessagesPerWindow)
        {
            throw new ProtocolException("rate_limit_exceeded", "Message rate limit exceeded.");
        }

        rateWindow.Enqueue(now);
        return ProtocolCodec.Deserialize(buffer.ToArray());
    }

    private static Task SendDirectAsync(WebSocket socket, byte[] message, CancellationToken cancellationToken) =>
        socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);

    private static void ValidateHello(ProtocolEnvelope hello)
    {
        if (hello.Kind != "hello" || hello.Type != "session.hello")
        {
            throw new ProtocolException("invalid_message", "The first message must be session.hello.");
        }
    }

    private static void ValidateHelloPayload(HelloPayload hello)
    {
        if (string.IsNullOrWhiteSpace(hello.PortalVersion) ||
            hello.PortalVersion.Length > 128 ||
            hello.SupportedProtocolVersions == null ||
            hello.SupportedProtocolVersions.Count is < 1 or > 16 ||
            hello.SupportedProtocolVersions.Any(version => version < 1) ||
            hello.SupportedProtocolVersions.Distinct().Count() != hello.SupportedProtocolVersions.Count)
        {
            throw new ProtocolException("invalid_message", "session.hello payload is invalid.");
        }
    }
}
