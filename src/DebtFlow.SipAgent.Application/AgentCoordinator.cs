using System.Text.Json;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Application;

public sealed class AgentCoordinator : IAsyncDisposable
{
    private readonly ISipRuntime _sipRuntime;
    private readonly IAgentEventStore _eventStore;
    private readonly IAgentEventPublisher _publisher;
    private readonly IAgentClock _clock;
    private readonly IAgentIdGenerator _ids;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _agentSessionId;
    private RegistrationState _registrationState = RegistrationState.Unconfigured;
    private CallSessionState? _call;
    private string _agentState = "ready";

    public AgentCoordinator(
        ISipRuntime sipRuntime,
        IAgentEventStore eventStore,
        IAgentEventPublisher publisher,
        IAgentClock clock,
        IAgentIdGenerator ids,
        bool durableStoreAvailable = true)
    {
        _sipRuntime = sipRuntime;
        _eventStore = eventStore;
        _publisher = publisher;
        _clock = clock;
        _ids = ids;
        _agentSessionId = ids.NewId();
        _agentState = durableStoreAvailable ? "ready" : "degraded";
        _sipRuntime.Signal += OnSipSignalAsync;
    }

    public string AgentSessionId => _agentSessionId;
    public string AgentInstanceId => _eventStore.AgentInstanceId;

    public async Task ConfigureAsync(ConfigureCommand command, CancellationToken cancellationToken)
    {
        ValidateConfigure(command);
        bool stopExistingRegistration;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_call is { State: not CallState.Ended })
            {
                throw new AgentCommandException("call_invalid_state");
            }

            stopExistingRegistration = _registrationState is not RegistrationState.Unconfigured and not RegistrationState.Unregistered;
        }
        finally
        {
            _gate.Release();
        }

        if (stopExistingRegistration)
        {
            await _sipRuntime.StopRegistrationAsync(cancellationToken);
        }

        await _sipRuntime.ConfigureAsync(
            new SipConfiguration(command.Host.Trim(), command.Port, command.Username.Trim(), command.Password),
            cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _registrationState = RegistrationState.Unregistered;
        }
        finally
        {
            _gate.Release();
        }

        await PublishRealtimeAsync(
            "registration.state_changed",
            new { state = "unregistered", reason = "configured" },
            cancellationToken);
    }

    public async Task StartRegistrationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_agentState != "ready")
            {
                throw new AgentCommandException("outbox_unavailable");
            }

            if (_registrationState == RegistrationState.Unconfigured)
            {
                throw new AgentCommandException("agent_not_configured");
            }

            _registrationState = RegistrationState.Registering;
        }
        finally
        {
            _gate.Release();
        }

        await PublishRealtimeAsync(
            "registration.state_changed",
            new { state = "registering" },
            cancellationToken);
        await _sipRuntime.StartRegistrationAsync(cancellationToken);
    }

    public async Task StopRegistrationAsync(CancellationToken cancellationToken)
    {
        await _sipRuntime.StopRegistrationAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _registrationState = RegistrationState.Unconfigured;
        }
        finally
        {
            _gate.Release();
        }

        await PublishRealtimeAsync(
            "registration.state_changed",
            new { state = "unconfigured", reason = "stopped" },
            cancellationToken);
    }

    public async Task StartCallAsync(CallStartCommand command, CancellationToken cancellationToken)
    {
        ProtocolCodec.ValidateUuid(command.CallId, "callId");
        if (string.IsNullOrWhiteSpace(command.Destination) || command.Destination.Length > 128)
        {
            throw new AgentCommandException("invalid_message");
        }

        if (string.IsNullOrWhiteSpace(command.ContextToken) || command.ContextToken.Length > 2048)
        {
            throw new AgentCommandException("invalid_message");
        }

        DateTimeOffset now = _clock.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_registrationState != RegistrationState.Registered)
            {
                throw new AgentCommandException("registration_unavailable");
            }

            if (_agentState != "ready")
            {
                throw new AgentCommandException("outbox_unavailable");
            }

            if (_call is { State: not CallState.Ended })
            {
                throw new AgentCommandException("call_invalid_state");
            }

            _call = CallReducer.CreateOutbound(
                command.CallId,
                command.CommandId,
                now,
                SensitiveValueMasker.MaskRemoteParty(command.Destination));
        }
        finally
        {
            _gate.Release();
        }

        await EmitCallEventAsync("call.created", _call, new
        {
            direction = "outbound",
            remoteParty = _call.MaskedRemoteParty
        }, cancellationToken);
        await ApplyCallSignalAsync(
            new CallSignal(CallSignalType.Dial, now),
            new { direction = "outbound" },
            cancellationToken);

        _ = RunOutboundCallAsync(command.Destination);
    }

    public async Task AnswerAsync(CallCommand command, CancellationToken cancellationToken)
    {
        EnsureCurrentCall(command.CallId, CallDirection.Inbound);
        await ApplyCallSignalAsync(
            new CallSignal(CallSignalType.Answering, _clock.UtcNow),
            new { direction = "inbound" },
            cancellationToken);
        await _sipRuntime.AnswerAsync(cancellationToken);
    }

    public async Task RejectAsync(CallCommand command, CancellationToken cancellationToken)
    {
        EnsureCurrentCall(command.CallId, CallDirection.Inbound);
        await ApplyCallSignalAsync(
            new CallSignal(CallSignalType.Ending, _clock.UtcNow),
            new { reason = "local_reject" },
            cancellationToken);
        await _sipRuntime.RejectAsync(cancellationToken);
        await EndCallAsync(CallOutcome.Rejected, "local_reject", cancellationToken);
    }

    public async Task HangupAsync(CallCommand command, CancellationToken cancellationToken)
    {
        CallSessionState call = EnsureCurrentCall(command.CallId);
        await ApplyCallSignalAsync(
            new CallSignal(CallSignalType.Ending, _clock.UtcNow),
            new { reason = "local_hangup" },
            cancellationToken);
        await _sipRuntime.HangupAsync(cancellationToken);
        CallOutcome outcome = call.AnsweredAtUtc.HasValue ? CallOutcome.Completed : CallOutcome.Cancelled;
        await EndCallAsync(outcome, "local_hangup", cancellationToken);
    }

    public async Task SendDtmfAsync(DtmfCommand command, CancellationToken cancellationToken)
    {
        CallSessionState call = EnsureCurrentCall(command.CallId);
        if (call.State != CallState.Connected || command.Digit.Length != 1 || !IsValidDtmf(command.Digit[0]))
        {
            throw new AgentCommandException("call_invalid_state");
        }

        await _sipRuntime.SendDtmfAsync(command.Digit[0], cancellationToken);
        await EmitCallEventAsync(
            "call.dtmf_sent",
            call,
            new { success = true },
            cancellationToken);
    }

    public async Task<AgentSnapshotPayload> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<ActiveCallSnapshot> calls = _call is { State: not CallState.Ended } call
                ? [new ActiveCallSnapshot(
                    call.CallId,
                    ToWire(call.Direction),
                    ToWire(call.State),
                    call.StartedAtUtc,
                    call.AnsweredAtUtc)]
                : [];

            return new AgentSnapshotPayload(
                _agentState,
                ToWire(_registrationState),
                _sipRuntime.AudioState,
                calls,
                await _eventStore.CountPendingAsync(cancellationToken),
                _eventStore.LastSequence);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sipRuntime.Signal -= OnSipSignalAsync;
        _gate.Dispose();
        await _sipRuntime.DisposeAsync();
    }

    private async Task RunOutboundCallAsync(string destination)
    {
        try
        {
            await _sipRuntime.StartCallAsync(destination, CancellationToken.None);
        }
        catch
        {
            await EndCallAsync(CallOutcome.Failed, "sip_runtime_error", CancellationToken.None);
        }
    }

    private async Task OnSipSignalAsync(SipSignal signal)
    {
        switch (signal.Type)
        {
            case SipSignalType.RegistrationRegistering:
            case SipSignalType.RegistrationRegistered:
            case SipSignalType.RegistrationUnregistered:
            case SipSignalType.RegistrationFailed:
                await ApplyRegistrationSignalAsync(signal);
                break;

            case SipSignalType.IncomingCall:
                await StartIncomingCallAsync(signal.Caller);
                break;

            case SipSignalType.CallTrying:
                await ApplyCallSignalAsync(
                    new CallSignal(CallSignalType.Trying, _clock.UtcNow),
                    new { sipStatusCode = signal.SipStatusCode },
                    CancellationToken.None);
                break;

            case SipSignalType.CallRinging:
                await ApplyCallSignalAsync(
                    new CallSignal(CallSignalType.Ringing, _clock.UtcNow),
                    new { sipStatusCode = signal.SipStatusCode },
                    CancellationToken.None);
                break;

            case SipSignalType.CallConnected:
                await ApplyCallSignalAsync(
                    new CallSignal(CallSignalType.Connected, _clock.UtcNow),
                    new { sipStatusCode = signal.SipStatusCode },
                    CancellationToken.None);
                break;

            case SipSignalType.CallFailed:
                await EndCallAsync(NormalizeFailure(signal.SipStatusCode), signal.SafeCode ?? "sip_failed", CancellationToken.None);
                break;

            case SipSignalType.CallRemoteEnded:
                await EndCallAsync(CallOutcome.Completed, "remote_hangup", CancellationToken.None);
                break;

            case SipSignalType.IncomingCancelled:
                await EndCallAsync(CallOutcome.Missed, "remote_cancel", CancellationToken.None);
                break;

            case SipSignalType.MediaReady:
                await EmitForCurrentCallAsync("call.media_ready", new { codec = signal.Codec ?? "unknown" });
                break;

            case SipSignalType.MediaDegraded:
                await PublishRealtimeAsync(
                    "audio.state_changed",
                    new { state = "degraded", code = signal.SafeCode ?? "audio_error" },
                    CancellationToken.None);
                await EmitForCurrentCallAsync(
                    "call.media_degraded",
                    new { code = signal.SafeCode ?? "audio_error" });
                break;

            case SipSignalType.DtmfReceived:
                await EmitForCurrentCallAsync("call.dtmf_received", new { received = true });
                break;
        }
    }

    private async Task ApplyRegistrationSignalAsync(SipSignal signal)
    {
        RegistrationState state;
        await _gate.WaitAsync();
        try
        {
            _registrationState = RegistrationReducer.Apply(_registrationState, signal.Type);
            state = _registrationState;
        }
        finally
        {
            _gate.Release();
        }

        await PublishRealtimeAsync(
            "registration.state_changed",
            new { state = ToWire(state), code = signal.SafeCode },
            CancellationToken.None);
    }

    private async Task StartIncomingCallAsync(string? caller)
    {
        CallSessionState incoming;
        await _gate.WaitAsync();
        try
        {
            if (_call is { State: not CallState.Ended })
            {
                return;
            }

            incoming = CallReducer.CreateInbound(
                _ids.NewId(),
                _clock.UtcNow,
                SensitiveValueMasker.MaskRemoteParty(caller));
            _call = incoming;
        }
        finally
        {
            _gate.Release();
        }

        await EmitCallEventAsync(
            "call.created",
            incoming,
            new { direction = "inbound", remoteParty = incoming.MaskedRemoteParty },
            CancellationToken.None);
        await ApplyCallSignalAsync(
            new CallSignal(CallSignalType.Incoming, _clock.UtcNow),
            new { direction = "inbound" },
            CancellationToken.None);
    }

    private async Task ApplyCallSignalAsync(CallSignal signal, object data, CancellationToken cancellationToken)
    {
        CallSessionState? changed = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_call == null)
            {
                return;
            }

            CallTransition transition = CallReducer.Apply(_call, signal);
            if (!transition.Accepted || !transition.StateChanged)
            {
                return;
            }

            _call = transition.State;
            changed = _call;
        }
        finally
        {
            _gate.Release();
        }

        await EmitCallEventAsync("call.state_changed", changed, data, cancellationToken);
    }

    private async Task EndCallAsync(CallOutcome outcome, string reason, CancellationToken cancellationToken)
    {
        CallSessionState? ended = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_call == null || _call.State == CallState.Ended)
            {
                return;
            }

            CallTransition transition = CallReducer.Apply(
                _call,
                new CallSignal(CallSignalType.End, _clock.UtcNow, outcome, reason));
            if (!transition.Accepted || !transition.StateChanged)
            {
                return;
            }

            _call = transition.State;
            ended = _call;
        }
        finally
        {
            _gate.Release();
        }

        await EmitCallEventAsync(
            "call.state_changed",
            ended,
            new { outcome = ToWire(outcome), endReason = reason },
            cancellationToken);

        long? talkDurationMs = ended.AnsweredAtUtc.HasValue
            ? Math.Max(0, (long)(ended.EndedAtUtc!.Value - ended.AnsweredAtUtc.Value).TotalMilliseconds)
            : null;
        long totalDurationMs = Math.Max(0, (long)(ended.EndedAtUtc!.Value - ended.StartedAtUtc).TotalMilliseconds);
        await EmitCallEventAsync(
            "call.ended",
            ended,
            new
            {
                direction = ToWire(ended.Direction),
                outcome = ToWire(outcome),
                endReason = reason,
                talkDurationMs,
                totalDurationMs
            },
            cancellationToken);
    }

    private async Task EmitForCurrentCallAsync(string eventType, object data)
    {
        CallSessionState? call;
        await _gate.WaitAsync();
        try
        {
            call = _call is { State: not CallState.Ended } current ? current : null;
        }
        finally
        {
            _gate.Release();
        }

        if (call != null)
        {
            await EmitCallEventAsync(eventType, call, data, CancellationToken.None);
        }
    }

    private async Task EmitCallEventAsync(
        string eventType,
        CallSessionState call,
        object data,
        CancellationToken cancellationToken)
    {
        string dataJson = JsonSerializer.Serialize(data, ProtocolJson.Options);
        StoredDurableEvent stored = await _eventStore.AppendAsync(
            new DurableEventDraft(
                _ids.NewId(),
                _agentSessionId,
                call.CallId,
                call.CommandId,
                eventType,
                _clock.UtcNow,
                ToWire(call.State),
                dataJson),
            cancellationToken);
        await _publisher.PublishDurableAsync(stored, cancellationToken);
    }

    private async Task PublishRealtimeAsync(string type, object data, CancellationToken cancellationToken)
    {
        JsonElement element = JsonSerializer.SerializeToElement(data, ProtocolJson.Options);
        await _publisher.PublishRealtimeAsync(
            type,
            new RealtimeEventPayload(
                "realtime",
                _ids.NewId(),
                AgentInstanceId,
                AgentSessionId,
                _clock.UtcNow,
                element),
            cancellationToken);
    }

    private CallSessionState EnsureCurrentCall(string callId, CallDirection? direction = null)
    {
        CallSessionState? call = _call;
        if (call == null || call.State == CallState.Ended || !string.Equals(call.CallId, callId, StringComparison.Ordinal))
        {
            throw new AgentCommandException("call_not_found");
        }

        if (direction.HasValue && call.Direction != direction.Value)
        {
            throw new AgentCommandException("call_invalid_state");
        }

        return call;
    }

    private static void ValidateConfigure(ConfigureCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Host) || command.Host.Length > 255 ||
            command.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(command.Username) || command.Username.Length > 128 ||
            string.IsNullOrEmpty(command.Password) || command.Password.Length > 512)
        {
            throw new AgentCommandException("invalid_message");
        }
    }

    private static bool IsValidDtmf(char value) =>
        value is >= '0' and <= '9' or '*' or '#' or 'A' or 'B' or 'C' or 'D' or 'a' or 'b' or 'c' or 'd';

    private static CallOutcome NormalizeFailure(int? statusCode) => statusCode switch
    {
        486 or 600 => CallOutcome.Busy,
        408 => CallOutcome.NoAnswer,
        _ => CallOutcome.Failed
    };

    public static string ToWire<T>(T value) where T : struct, Enum =>
        value.ToString().Replace("NoAnswer", "no_answer", StringComparison.Ordinal).ToLowerInvariant();
}

public sealed class AgentCommandException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
