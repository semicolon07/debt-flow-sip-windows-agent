using System.Text.Json;
using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Application;

public sealed class AgentCoordinator : IAsyncDisposable
{
    private const int QueueCapacity = 512;
    private readonly ISipRuntime _sipRuntime;
    private readonly IAgentEventStore _eventStore;
    private readonly IAgentEventPublisher _publisher;
    private readonly IAgentClock _clock;
    private readonly IAgentIdGenerator _ids;
    private readonly IAgentDelay _delay;
    private readonly IAgentDelay _retryDelay;
    private readonly IRegistrationRetryPolicy _retryPolicy;
    private readonly TimeSpan _ownerDisconnectGrace;
    private readonly Channel<CoordinatorWorkItem> _work;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _processor;
    private readonly string _agentSessionId;
    private Task _ownerLeaseExpiration = Task.CompletedTask;
    private Task _registrationRetry = Task.CompletedTask;
    private RegistrationState _registrationState = RegistrationState.Unconfigured;
    private CallSessionState? _call;
    private string _agentState;
    private string? _agentStateCode;
    private bool _portalConnected;
    private bool _clearCredentialsAfterCall;
    private long _ownerLeaseGeneration;
    private long _registrationGeneration;
    private int _registrationRetryAttempt;
    private bool _registrationRequested;
    private int _disposeStarted;

    public AgentCoordinator(
        ISipRuntime sipRuntime,
        IAgentEventStore eventStore,
        IAgentEventPublisher publisher,
        IAgentClock clock,
        IAgentIdGenerator ids,
        bool durableStoreAvailable = true,
        IAgentDelay? delay = null,
        TimeSpan? ownerDisconnectGrace = null,
        string? initialDegradedCode = null,
        IAgentDelay? retryDelay = null,
        IRegistrationRetryPolicy? retryPolicy = null)
    {
        _sipRuntime = sipRuntime;
        _eventStore = eventStore;
        _publisher = publisher;
        _clock = clock;
        _ids = ids;
        _delay = delay ?? new SystemAgentDelay();
        _retryDelay = retryDelay ?? new SystemAgentDelay();
        _retryPolicy = retryPolicy ?? new JitteredRegistrationRetryPolicy();
        _ownerDisconnectGrace = ownerDisconnectGrace ?? TimeSpan.FromSeconds(60);
        _agentSessionId = ids.NewId();
        _agentState = durableStoreAvailable && initialDegradedCode == null ? "ready" : "degraded";
        _agentStateCode = durableStoreAvailable
            ? initialDegradedCode
            : initialDegradedCode ?? "outbox_unavailable";
        _work = Channel.CreateBounded<CoordinatorWorkItem>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _sipRuntime.Signal += OnSipSignalAsync;
        _processor = ProcessAsync();
    }

    public string AgentSessionId => _agentSessionId;
    public string AgentInstanceId => _eventStore.AgentInstanceId;

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(InitializeCoreAsync, cancellationToken);

    public Task ConfigureAsync(ConfigureCommand command, CancellationToken cancellationToken) =>
        EnqueueAsync(token => ConfigureCoreAsync(command, token), cancellationToken);

    public Task StartRegistrationAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(StartRegistrationCoreAsync, cancellationToken);

    public Task StopRegistrationAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(StopRegistrationCoreAsync, cancellationToken);

    public Task StartCallAsync(CallStartCommand command, CancellationToken cancellationToken) =>
        EnqueueAsync(token => StartCallCoreAsync(command, token), cancellationToken);

    public Task AnswerAsync(CallCommand command, CancellationToken cancellationToken) =>
        EnqueueAsync(token => AnswerCoreAsync(command, token), cancellationToken);

    public Task RejectAsync(CallCommand command, CancellationToken cancellationToken) =>
        EnqueueAsync(token => RejectCoreAsync(command, token), cancellationToken);

    public Task HangupAsync(CallCommand command, CancellationToken cancellationToken) =>
        EnqueueAsync(token => HangupCoreAsync(command, token), cancellationToken);

    public Task SendDtmfAsync(DtmfCommand command, CancellationToken cancellationToken) =>
        EnqueueAsync(token => SendDtmfCoreAsync(command, token), cancellationToken);

    public Task<AgentSnapshotPayload> GetSnapshotAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(GetSnapshotCoreAsync, cancellationToken);

    public Task PortalConnectedAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(PortalConnectedCoreAsync, cancellationToken);

    public async Task PortalDisconnectedAsync(CancellationToken cancellationToken)
    {
        long generation = await EnqueueAsync(PortalDisconnectedCoreAsync, cancellationToken);
        Volatile.Write(ref _ownerLeaseExpiration, ExpireOwnerLeaseAsync(generation));
    }

    internal Task WaitForCurrentOwnerLeaseExpirationAsync() =>
        Volatile.Read(ref _ownerLeaseExpiration);

    public Task ShutdownAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(ShutdownCoreAsync, cancellationToken, cancelExecution: true);

    public Task MarkDegradedAsync(string code, CancellationToken cancellationToken) =>
        EnqueueAsync(token => MarkDegradedCoreAsync(code, token), cancellationToken);

    public Task RefreshStorageHealthAsync(CancellationToken cancellationToken) =>
        EnqueueAsync(RefreshStorageHealthCoreAsync, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ShutdownAsync(timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
        {
        }

        _sipRuntime.Signal -= OnSipSignalAsync;
        _work.Writer.TryComplete();
        await _processor;
        _lifetime.Cancel();
        await _sipRuntime.DisposeAsync();
        _lifetime.Dispose();
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            CallSessionState? recovered = await _eventStore.LoadActiveCallAsync(cancellationToken);
            if (recovered != null)
            {
                _call = recovered;
                if (recovered.State == CallState.Ended)
                {
                    await EmitEndedEventAsync(
                        recovered,
                        recovered.Outcome ?? CallOutcome.Failed,
                        recovered.EndReason ?? "agent_restarted",
                        cancellationToken);
                }
                else
                {
                    await EndCallCoreAsync(CallOutcome.Failed, "agent_restarted", cancellationToken);
                }
            }

            await _eventStore.PruneCommandsAsync(
                _clock.UtcNow.AddDays(-30),
                50_000,
                cancellationToken);
            await RefreshStorageHealthCoreAsync(cancellationToken);
        }
        catch (AgentCommandException exception)
        {
            await MarkDegradedCoreAsync(exception.ErrorCode, CancellationToken.None);
        }
        catch (AgentStoreException exception)
        {
            await MarkDegradedCoreAsync(exception.ErrorCode, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkDegradedCoreAsync("outbox_unavailable", CancellationToken.None);
        }
    }

    private async Task RefreshStorageHealthCoreAsync(CancellationToken cancellationToken)
    {
        EventStoreHealth health = await _eventStore.GetHealthAsync(cancellationToken);
        if (health.CapacityState is EventStoreCapacityState.Critical or EventStoreCapacityState.Full)
        {
            await MarkDegradedCoreAsync(
                health.CapacityState == EventStoreCapacityState.Full
                    ? "outbox_capacity_exceeded"
                    : "outbox_capacity_critical",
                cancellationToken);
            if ((_call is null or { State: CallState.Ended }) &&
                _registrationState != RegistrationState.Unconfigured)
            {
                await StopRegistrationCoreAsync(cancellationToken);
            }

            return;
        }

        if (_agentState == "degraded" && IsCapacityCode(_agentStateCode))
        {
            _agentState = "ready";
            _agentStateCode = null;
            await PublishRealtimeAsync(
                "agent.state_changed",
                new { state = "ready", code = health.CapacityState == EventStoreCapacityState.Warning ? "outbox_capacity_warning" : null },
                cancellationToken);
        }
        else if (health.CapacityState == EventStoreCapacityState.Warning)
        {
            await PublishRealtimeAsync(
                "agent.storage_state_changed",
                new { state = "warning", code = "outbox_capacity_warning" },
                cancellationToken);
        }
    }

    private async Task ConfigureCoreAsync(ConfigureCommand command, CancellationToken cancellationToken)
    {
        EnsureOperational();
        SipConfiguration configuration = ValidateConfigure(command);
        if (_call is { State: not CallState.Ended })
        {
            throw new AgentCommandException("call_invalid_state");
        }

        if (_registrationState is not RegistrationState.Unconfigured and not RegistrationState.Unregistered)
        {
            await _sipRuntime.StopRegistrationAsync(cancellationToken);
        }

        _registrationRequested = false;
        _registrationRetryAttempt = 0;
        _registrationGeneration++;
        await _sipRuntime.ConfigureAsync(configuration, cancellationToken);
        _registrationState = RegistrationState.Unregistered;
        await PublishRealtimeAsync(
            "registration.state_changed",
            new { state = "unregistered", reason = "configured" },
            cancellationToken);
    }

    private async Task StartRegistrationCoreAsync(CancellationToken cancellationToken)
    {
        EnsureOperational();
        if (_registrationState == RegistrationState.Unconfigured)
        {
            throw new AgentCommandException("agent_not_configured");
        }

        if (_registrationState is not RegistrationState.Unregistered and not RegistrationState.Failed)
        {
            throw new AgentCommandException("registration_unavailable");
        }

        _registrationRequested = true;
        _registrationRetryAttempt = 0;
        long generation = ++_registrationGeneration;
        await StartRegistrationAttemptCoreAsync(generation, cancellationToken);
    }

    private async Task StopRegistrationCoreAsync(CancellationToken cancellationToken)
    {
        _registrationRequested = false;
        _registrationRetryAttempt = 0;
        _registrationGeneration++;
        await _sipRuntime.StopRegistrationAsync(cancellationToken);
        _registrationState = RegistrationState.Unconfigured;
        await PublishRealtimeAsync(
            "registration.state_changed",
            new { state = "unconfigured", reason = "stopped" },
            cancellationToken);
    }

    private async Task StartCallCoreAsync(CallStartCommand command, CancellationToken cancellationToken)
    {
        ProtocolCodec.ValidateUuid(command.CallId, "callId");
        if (string.IsNullOrWhiteSpace(command.Destination) || command.Destination.Length > 128 ||
            string.IsNullOrWhiteSpace(command.ContextToken) || command.ContextToken.Length > 2048)
        {
            throw new AgentCommandException("invalid_message");
        }

        if (_registrationState != RegistrationState.Registered)
        {
            throw new AgentCommandException("registration_unavailable");
        }

        EnsureOperational();
        if (_call is { State: not CallState.Ended })
        {
            throw new AgentCommandException("call_invalid_state");
        }

        await EnsureCapacityForNewCallAsync(cancellationToken);

        _call = CallReducer.CreateOutbound(
            command.CallId,
            command.CommandId,
            _clock.UtcNow,
            SensitiveValueMasker.MaskRemoteParty(command.Destination));
        try
        {
            await EmitCallEventAsync(
                "call.created",
                _call,
                new { direction = "outbound", remoteParty = _call.MaskedRemoteParty },
                cancellationToken);
            await ApplyCallSignalCoreAsync(
                new CallSignal(CallSignalType.Dial, _clock.UtcNow),
                new { direction = "outbound" },
                cancellationToken);
        }
        catch (AgentCommandException exception) when (exception.ErrorCode == "outbox_unavailable")
        {
            _call = null;
            throw;
        }
        _ = RunOutboundCallAsync(command.Destination);
    }

    private async Task AnswerCoreAsync(CallCommand command, CancellationToken cancellationToken)
    {
        CallSessionState call = EnsureCurrentCall(command.CallId, CallDirection.Inbound);
        if (call.State != CallState.Incoming)
        {
            throw new AgentCommandException("call_invalid_state");
        }

        await ApplyCallSignalCoreAsync(
            new CallSignal(CallSignalType.Answering, _clock.UtcNow),
            new { direction = "inbound" },
            cancellationToken);
        await _sipRuntime.AnswerAsync(cancellationToken);
    }

    private async Task RejectCoreAsync(CallCommand command, CancellationToken cancellationToken)
    {
        CallSessionState call = EnsureCurrentCall(command.CallId, CallDirection.Inbound);
        if (call.State != CallState.Incoming)
        {
            throw new AgentCommandException("call_invalid_state");
        }

        await ApplyCallSignalCoreAsync(
            new CallSignal(CallSignalType.Ending, _clock.UtcNow),
            new { reason = "local_reject" },
            cancellationToken);
        await _sipRuntime.RejectAsync(cancellationToken);
        await EndCallCoreAsync(CallOutcome.Rejected, "local_reject", cancellationToken);
    }

    private async Task HangupCoreAsync(CallCommand command, CancellationToken cancellationToken)
    {
        CallSessionState call = EnsureCurrentCall(command.CallId);
        await ApplyCallSignalCoreAsync(
            new CallSignal(CallSignalType.Ending, _clock.UtcNow),
            new { reason = "local_hangup" },
            cancellationToken);
        await _sipRuntime.HangupAsync(cancellationToken);
        await EndCallCoreAsync(
            call.AnsweredAtUtc.HasValue ? CallOutcome.Completed : CallOutcome.Cancelled,
            "local_hangup",
            cancellationToken);
    }

    private async Task SendDtmfCoreAsync(DtmfCommand command, CancellationToken cancellationToken)
    {
        CallSessionState call = EnsureCurrentCall(command.CallId);
        if (call.State != CallState.Connected ||
            string.IsNullOrEmpty(command.Digit) ||
            command.Digit.Length != 1 ||
            !IsValidDtmf(command.Digit[0]))
        {
            throw new AgentCommandException("call_invalid_state");
        }

        await _sipRuntime.SendDtmfAsync(command.Digit[0], cancellationToken);
        await EmitCallEventAsync("call.dtmf_sent", call, new { success = true }, cancellationToken);
    }

    private async Task<AgentSnapshotPayload> GetSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ActiveCallSnapshot> calls = _call is { State: not CallState.Ended } call
            ? [new ActiveCallSnapshot(call.CallId, ToWire(call.Direction), ToWire(call.State), call.StartedAtUtc, call.AnsweredAtUtc)]
            : [];
        long pendingEventCount;
        try
        {
            pendingEventCount = await _eventStore.CountPendingAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkDegradedCoreAsync("outbox_unavailable", CancellationToken.None);
            pendingEventCount = 0;
        }

        return new AgentSnapshotPayload(
            _agentState,
            ToWire(_registrationState),
            _sipRuntime.AudioState,
            calls,
            pendingEventCount,
            _eventStore.LastSequence,
            _agentStateCode);
    }

    private Task PortalConnectedCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _portalConnected = true;
        _clearCredentialsAfterCall = false;
        _ownerLeaseGeneration++;
        return Task.CompletedTask;
    }

    private Task<long> PortalDisconnectedCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _portalConnected = false;
        return Task.FromResult(++_ownerLeaseGeneration);
    }

    private async Task ExpireOwnerLeaseCoreAsync(long generation, CancellationToken cancellationToken)
    {
        if (_portalConnected || generation != _ownerLeaseGeneration)
        {
            return;
        }

        if (_call is { State: not CallState.Ended })
        {
            _clearCredentialsAfterCall = true;
            return;
        }

        if (_registrationState != RegistrationState.Unconfigured)
        {
            await StopRegistrationCoreAsync(cancellationToken);
        }
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        if (_agentState == "stopping")
        {
            return;
        }

        _agentState = "stopping";
        _agentStateCode = null;
        await PublishRealtimeAsync("agent.state_changed", new { state = "stopping" }, cancellationToken);
        if (_call is { State: not CallState.Ended } active)
        {
            try
            {
                await _sipRuntime.HangupAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }

            try
            {
                await EndCallCoreAsync(
                    active.AnsweredAtUtc.HasValue ? CallOutcome.Completed : CallOutcome.Cancelled,
                    "agent_shutdown",
                    cancellationToken);
            }
            catch (AgentCommandException exception) when (exception.ErrorCode == "outbox_unavailable")
            {
            }
        }

        if (_registrationState != RegistrationState.Unconfigured)
        {
            try
            {
                await StopRegistrationCoreAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _registrationState = RegistrationState.Unconfigured;
            }
        }

        _portalConnected = false;
        _ownerLeaseGeneration++;
        try
        {
            await _eventStore.CheckpointAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    private async Task MarkDegradedCoreAsync(string code, CancellationToken cancellationToken)
    {
        if (_agentState == "stopping" || (_agentState == "degraded" && string.Equals(_agentStateCode, code, StringComparison.Ordinal)))
        {
            return;
        }

        _agentState = "degraded";
        _agentStateCode = code;
        await PublishRealtimeAsync(
            "agent.state_changed",
            new { state = "degraded", code },
            cancellationToken);
    }

    private async Task RunOutboundCallAsync(string destination)
    {
        try
        {
            await _sipRuntime.StartCallAsync(destination, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            try
            {
                await EnqueueAsync(
                    token => EndCallCoreAsync(CallOutcome.Failed, "sip_runtime_error", token),
                    CancellationToken.None);
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    private Task OnSipSignalAsync(SipSignal signal) =>
        EnqueueAsync(token => ApplySipSignalCoreAsync(signal, token), CancellationToken.None);

    private async Task ApplySipSignalCoreAsync(SipSignal signal, CancellationToken cancellationToken)
    {
        switch (signal.Type)
        {
            case SipSignalType.RegistrationRegistering:
            case SipSignalType.RegistrationRegistered:
            case SipSignalType.RegistrationUnregistered:
            case SipSignalType.RegistrationFailed:
                _registrationState = RegistrationReducer.Apply(_registrationState, signal.Type);
                await PublishRealtimeAsync(
                    "registration.state_changed",
                    new { state = ToWire(_registrationState), code = signal.SafeCode },
                    cancellationToken);
                if (signal.Type == SipSignalType.RegistrationRegistered)
                {
                    long generation = ++_registrationGeneration;
                    Volatile.Write(
                        ref _registrationRetry,
                        ResetRegistrationRetryAfterStablePeriodAsync(generation));
                }
                else if (signal.Type == SipSignalType.RegistrationFailed &&
                         signal.Retryable &&
                         _registrationRequested &&
                         _agentState == "ready")
                {
                    await ScheduleRegistrationRetryCoreAsync(signal.SafeCode, cancellationToken);
                }
                break;
            case SipSignalType.IncomingCall:
                await StartIncomingCallCoreAsync(signal.Caller, cancellationToken);
                break;
            case SipSignalType.CallTrying:
                await ApplyCallSignalCoreAsync(new CallSignal(CallSignalType.Trying, _clock.UtcNow), new { sipStatusCode = signal.SipStatusCode }, cancellationToken);
                break;
            case SipSignalType.CallRinging:
                await ApplyCallSignalCoreAsync(new CallSignal(CallSignalType.Ringing, _clock.UtcNow), new { sipStatusCode = signal.SipStatusCode }, cancellationToken);
                break;
            case SipSignalType.CallConnected:
                await ApplyCallSignalCoreAsync(new CallSignal(CallSignalType.Connected, _clock.UtcNow), new { sipStatusCode = signal.SipStatusCode }, cancellationToken);
                break;
            case SipSignalType.CallFailed:
                await EndCallCoreAsync(NormalizeFailure(signal.SipStatusCode), signal.SafeCode ?? "sip_failed", cancellationToken);
                break;
            case SipSignalType.CallRemoteEnded:
                await EndCallCoreAsync(CallOutcome.Completed, "remote_hangup", cancellationToken);
                break;
            case SipSignalType.IncomingCancelled:
                await EndCallCoreAsync(CallOutcome.Missed, "remote_cancel", cancellationToken);
                break;
            case SipSignalType.MediaReady:
                await EmitForCurrentCallCoreAsync("call.media_ready", new { codec = signal.Codec ?? "unknown" }, cancellationToken);
                break;
            case SipSignalType.MediaDegraded:
                await PublishRealtimeAsync(
                    "audio.state_changed",
                    new { state = "degraded", code = signal.SafeCode ?? "audio_error" },
                    cancellationToken);
                await EmitForCurrentCallCoreAsync("call.media_degraded", new { code = signal.SafeCode ?? "audio_error" }, cancellationToken);
                break;
            case SipSignalType.AudioInventoryChanged:
                await PublishRealtimeAsync(
                    "audio.state_changed",
                    new { state = _sipRuntime.AudioState, code = signal.SafeCode ?? "audio_devices_changed" },
                    cancellationToken);
                break;
            case SipSignalType.DtmfReceived:
                await EmitForCurrentCallCoreAsync("call.dtmf_received", new { received = true }, cancellationToken);
                break;
        }
    }

    private async Task StartIncomingCallCoreAsync(string? caller, CancellationToken cancellationToken)
    {
        if (_call is { State: not CallState.Ended })
        {
            return;
        }

        try
        {
            await EnsureCapacityForNewCallAsync(cancellationToken);
        }
        catch (AgentCommandException exception) when (IsCapacityCode(exception.ErrorCode))
        {
            await _sipRuntime.RejectUnavailableAsync(cancellationToken);
            if (_registrationState != RegistrationState.Unconfigured)
            {
                await StopRegistrationCoreAsync(cancellationToken);
            }

            return;
        }

        _call = CallReducer.CreateInbound(_ids.NewId(), _clock.UtcNow, SensitiveValueMasker.MaskRemoteParty(caller));
        try
        {
            await EmitCallEventAsync(
                "call.created",
                _call,
                new { direction = "inbound", remoteParty = _call.MaskedRemoteParty },
                cancellationToken);
            await ApplyCallSignalCoreAsync(
                new CallSignal(CallSignalType.Incoming, _clock.UtcNow),
                new { direction = "inbound" },
                cancellationToken);
        }
        catch (AgentCommandException exception) when (exception.ErrorCode == "outbox_unavailable")
        {
            await _sipRuntime.RejectUnavailableAsync(cancellationToken);
            _call = null;
            return;
        }

        if (!_portalConnected)
        {
            await ApplyCallSignalCoreAsync(
                new CallSignal(CallSignalType.Ending, _clock.UtcNow),
                new { reason = "portal_unavailable" },
                cancellationToken);
            await _sipRuntime.RejectUnavailableAsync(cancellationToken);
            await EndCallCoreAsync(CallOutcome.Rejected, "portal_unavailable", cancellationToken);
        }
    }

    private async Task ApplyCallSignalCoreAsync(CallSignal signal, object data, CancellationToken cancellationToken)
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
        await EmitCallEventAsync("call.state_changed", _call, data, cancellationToken);
    }

    private async Task EndCallCoreAsync(CallOutcome outcome, string reason, CancellationToken cancellationToken)
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
        CallSessionState ended = _call;
        await EmitCallEventAsync(
            "call.state_changed",
            ended,
            new { outcome = ToWire(outcome), endReason = reason },
            cancellationToken);
        await EmitEndedEventAsync(ended, outcome, reason, cancellationToken);
        await RefreshStorageHealthCoreAsync(cancellationToken);

        if (_clearCredentialsAfterCall && !_portalConnected)
        {
            _clearCredentialsAfterCall = false;
            if (_registrationState != RegistrationState.Unconfigured)
            {
                await StopRegistrationCoreAsync(cancellationToken);
            }
        }
    }

    private async Task EmitEndedEventAsync(
        CallSessionState ended,
        CallOutcome outcome,
        string reason,
        CancellationToken cancellationToken)
    {
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

    private Task EmitForCurrentCallCoreAsync(string eventType, object data, CancellationToken cancellationToken) =>
        _call is { State: not CallState.Ended } call
            ? EmitCallEventAsync(eventType, call, data, cancellationToken)
            : Task.CompletedTask;

    private async Task EmitCallEventAsync(
        string eventType,
        CallSessionState call,
        object data,
        CancellationToken cancellationToken)
    {
        try
        {
            StoredDurableEvent stored = await _eventStore.AppendCallEventAsync(
                new DurableEventDraft(
                    _ids.NewId(),
                    _agentSessionId,
                    call.CallId,
                    call.CommandId,
                    eventType,
                    _clock.UtcNow,
                    ToWire(call.State),
                    JsonSerializer.Serialize(data, ProtocolJson.Options)),
                call,
                string.Equals(eventType, "call.ended", StringComparison.Ordinal),
                cancellationToken);
            await _publisher.PublishDurableAsync(stored, cancellationToken);
        }
        catch (AgentStoreException exception)
        {
            await MarkDegradedCoreAsync(exception.ErrorCode, CancellationToken.None);
            throw new AgentCommandException(exception.ErrorCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not AgentCommandException)
        {
            await MarkDegradedCoreAsync("outbox_unavailable", CancellationToken.None);
            throw new AgentCommandException("outbox_unavailable");
        }
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

    private async Task EnsureCapacityForNewCallAsync(CancellationToken cancellationToken)
    {
        EventStoreHealth health;
        try
        {
            health = await _eventStore.GetHealthAsync(cancellationToken);
        }
        catch (AgentStoreException exception)
        {
            await MarkDegradedCoreAsync(exception.ErrorCode, CancellationToken.None);
            throw new AgentCommandException(exception.ErrorCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkDegradedCoreAsync("outbox_unavailable", CancellationToken.None);
            throw new AgentCommandException("outbox_unavailable");
        }

        if (health.CapacityState is EventStoreCapacityState.Critical or EventStoreCapacityState.Full)
        {
            string code = health.CapacityState == EventStoreCapacityState.Full
                ? "outbox_capacity_exceeded"
                : "outbox_capacity_critical";
            await MarkDegradedCoreAsync(code, CancellationToken.None);
            if ((_call is null or { State: CallState.Ended }) &&
                _registrationState != RegistrationState.Unconfigured)
            {
                await StopRegistrationCoreAsync(cancellationToken);
            }

            throw new AgentCommandException(code);
        }

        if (health.CapacityState == EventStoreCapacityState.Warning)
        {
            await PublishRealtimeAsync(
                "agent.storage_state_changed",
                new { state = "warning", code = "outbox_capacity_warning" },
                cancellationToken);
        }
    }

    private async Task StartRegistrationAttemptCoreAsync(long generation, CancellationToken cancellationToken)
    {
        if (!_registrationRequested || generation != _registrationGeneration)
        {
            return;
        }

        _registrationState = RegistrationState.Registering;
        await PublishRealtimeAsync("registration.state_changed", new { state = "registering" }, cancellationToken);
        try
        {
            await _sipRuntime.StartRegistrationAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _registrationState = RegistrationState.Failed;
            await PublishRealtimeAsync(
                "registration.state_changed",
                new { state = "failed", code = "registration_transport_failure" },
                cancellationToken);
            await ScheduleRegistrationRetryCoreAsync("registration_transport_failure", cancellationToken);
        }
    }

    private async Task ScheduleRegistrationRetryCoreAsync(string? code, CancellationToken cancellationToken)
    {
        if (!_registrationRequested || _agentState != "ready")
        {
            return;
        }

        int attempt = ++_registrationRetryAttempt;
        TimeSpan retryAfter = _retryPolicy.GetDelay(attempt);
        long generation = ++_registrationGeneration;
        _registrationState = RegistrationState.Retrying;
        await PublishRealtimeAsync(
            "registration.state_changed",
            new
            {
                state = "retrying",
                code = code ?? "registration_retry_scheduled",
                attempt,
                retryAfterMs = (long)retryAfter.TotalMilliseconds
            },
            cancellationToken);
        Volatile.Write(ref _registrationRetry, RetryRegistrationAfterDelayAsync(generation, retryAfter));
    }

    private async Task RetryRegistrationAfterDelayAsync(long generation, TimeSpan delay)
    {
        try
        {
            await _retryDelay.DelayAsync(delay, _lifetime.Token);
            await EnqueueAsync(
                token => StartRegistrationAttemptCoreAsync(generation, token),
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task ResetRegistrationRetryAfterStablePeriodAsync(long generation)
    {
        try
        {
            await _retryDelay.DelayAsync(TimeSpan.FromMinutes(5), _lifetime.Token);
            await EnqueueAsync(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (generation == _registrationGeneration && _registrationState == RegistrationState.Registered)
                    {
                        _registrationRetryAttempt = 0;
                    }

                    return Task.CompletedTask;
                },
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task ExpireOwnerLeaseAsync(long generation)
    {
        try
        {
            await _delay.DelayAsync(_ownerDisconnectGrace, _lifetime.Token);
            await EnqueueAsync(token => ExpireOwnerLeaseCoreAsync(generation, token), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task ProcessAsync()
    {
        await foreach (CoordinatorWorkItem item in _work.Reader.ReadAllAsync())
        {
            await item.ExecuteAsync(_lifetime.Token);
        }
    }

    private Task EnqueueAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken,
        bool cancelExecution = false) =>
        EnqueueAsync(async token =>
        {
            await action(token);
            return true;
        }, cancellationToken, cancelExecution);

    private async Task<T> EnqueueAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        bool cancelExecution = false)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _work.Writer.WriteAsync(
            new CoordinatorWorkItem<T>(action, completion, cancellationToken, cancelExecution),
            cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    private void EnsureOperational()
    {
        if (_agentState != "ready")
        {
            throw new AgentCommandException(_agentStateCode ?? "agent_unavailable");
        }
    }

    private static bool IsCapacityCode(string? code) =>
        code is "outbox_capacity_critical" or "outbox_capacity_exceeded";

    private CallSessionState EnsureCurrentCall(string callId, CallDirection? direction = null)
    {
        ProtocolCodec.ValidateUuid(callId, "callId");
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

    private static SipConfiguration ValidateConfigure(ConfigureCommand command)
    {
        string host = command.Host.Trim();
        if (!IPAddress.TryParse(host, out IPAddress? address) ||
            !string.Equals(host, address.ToString(), StringComparison.OrdinalIgnoreCase) ||
            address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0 ||
            command.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(command.Username) || command.Username.Length > 128 ||
            string.IsNullOrEmpty(command.Password) || command.Password.Length > 512)
        {
            throw new AgentCommandException("invalid_message");
        }

        return new SipConfiguration(
            address.ToString(),
            command.Port,
            command.Username.Trim(),
            command.Password);
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

    private abstract record CoordinatorWorkItem
    {
        public abstract Task ExecuteAsync(CancellationToken lifetimeToken);
    }

    private sealed record CoordinatorWorkItem<T>(
        Func<CancellationToken, Task<T>> Action,
        TaskCompletionSource<T> Completion,
        CancellationToken RequestCancellationToken,
        bool CancelExecution) : CoordinatorWorkItem
    {
        public override async Task ExecuteAsync(CancellationToken lifetimeToken)
        {
            if (RequestCancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(RequestCancellationToken);
                return;
            }

            try
            {
                CancellationToken executionToken = CancelExecution
                    ? RequestCancellationToken
                    : lifetimeToken;
                Completion.TrySetResult(await Action(executionToken));
            }
            catch (OperationCanceledException exception)
            {
                Completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
        }
    }
}

public sealed class AgentCommandException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}
