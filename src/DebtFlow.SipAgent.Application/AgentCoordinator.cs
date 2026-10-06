using System.Text.Json;
using System.Threading.Channels;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
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
    private readonly Task _signalPump;
    private readonly string _agentSessionId;
    private Task _ownerLeaseExpiration = Task.CompletedTask;
    private Task _registrationRetry = Task.CompletedTask;
    private RegistrationState _registrationState = RegistrationState.Unconfigured;
    private CallSessionState? _call;
    private SipCallHandle? _activeSipCall;
    private string _agentState;
    private string? _agentStateCode;
    private bool _portalConnected;
    private bool _clearCredentialsAfterCall;
    private string? _collectionId;
    private string? _collectionBindingId;
    private long _ownerLeaseGeneration;
    private long _registrationGeneration;
    private long _callGeneration;
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
        _processor = ProcessAsync();
        _signalPump = PumpSipSignalsAsync();
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

    public async Task AnswerAsync(CallCommand command, CancellationToken cancellationToken)
    {
        CallRuntimeOperation operation = await EnqueueAsync(
            token => PrepareAnswerCoreAsync(command, token),
            cancellationToken);
        await ExecuteRuntimeOperationAsync(
            operation,
            token => _sipRuntime.AnswerAsync(operation.Handle, token),
            TimeSpan.FromSeconds(35));
    }

    public async Task RejectAsync(CallCommand command, CancellationToken cancellationToken)
    {
        CallRuntimeOperation operation = await EnqueueAsync(
            token => PrepareEndingCoreAsync(command, CallDirection.Inbound, "local_reject", token),
            cancellationToken);
        await ExecuteRuntimeOperationAsync(
            operation,
            token => _sipRuntime.RejectAsync(operation.Handle, token),
            TimeSpan.FromSeconds(10));
        await EnqueueAsync(
            token => EndCallForHandleCoreAsync(
                operation.Handle,
                CallOutcome.Rejected,
                "local_reject",
                token),
            CancellationToken.None);
    }

    public async Task HangupAsync(CallCommand command, CancellationToken cancellationToken)
    {
        CallRuntimeOperation operation = await EnqueueAsync(
            token => PrepareEndingCoreAsync(command, null, "local_hangup", token),
            cancellationToken);
        await ExecuteRuntimeOperationAsync(
            operation,
            token => _sipRuntime.HangupAsync(operation.Handle, token),
            TimeSpan.FromSeconds(10));
        await EnqueueAsync(
            token => EndCallForHandleCoreAsync(
                operation.Handle,
                operation.Call.AnsweredAtUtc.HasValue ? CallOutcome.Completed : CallOutcome.Cancelled,
                "local_hangup",
                token),
            CancellationToken.None);
    }

    public async Task SendDtmfAsync(DtmfCommand command, CancellationToken cancellationToken)
    {
        CallRuntimeOperation operation = await EnqueueAsync(
            token => PrepareDtmfCoreAsync(command, token),
            cancellationToken);
        await ExecuteRuntimeOperationAsync(
            operation,
            token => _sipRuntime.SendDtmfAsync(operation.Handle, command.Digit[0], token),
            TimeSpan.FromSeconds(10));
        await EnqueueAsync(
            token => EmitForHandleCoreAsync(
                operation.Handle,
                "call.dtmf_sent",
                new { success = true },
                token),
            CancellationToken.None);
    }

    public Task SetMicrophoneMutedAsync(CallMuteCommand command, CancellationToken cancellationToken) =>
        ExecuteAudioControlAsync(
            command.CallId,
            cancellationToken,
            (call, token) => _sipRuntime.SetMicrophoneMutedAsync(call, command.Muted, token));

    public Task SetOutputVolumeAsync(CallVolumeCommand command, CancellationToken cancellationToken)
    {
        ValidateVolume(command.Volume);
        return ExecuteAudioControlAsync(
            command.CallId,
            cancellationToken,
            (call, token) => _sipRuntime.SetOutputVolumeAsync(call, command.Volume, token));
    }

    public Task SetInputVolumeAsync(CallVolumeCommand command, CancellationToken cancellationToken)
    {
        ValidateVolume(command.Volume);
        return ExecuteAudioControlAsync(
            command.CallId,
            cancellationToken,
            (call, token) => _sipRuntime.SetInputVolumeAsync(call, command.Volume, token));
    }

    public Task SetOutputVolumePreferenceAsync(
        AudioVolumePreferenceCommand command,
        CancellationToken cancellationToken)
    {
        ValidateVolume(command.Volume);
        return ExecuteAudioPreferenceAsync(
            cancellationToken,
            (call, token) => _sipRuntime.SetOutputVolumeAsync(call, command.Volume, token),
            token => _sipRuntime.SetOutputVolumePreferenceAsync(command.Volume, token));
    }

    public Task SetInputVolumePreferenceAsync(
        AudioVolumePreferenceCommand command,
        CancellationToken cancellationToken)
    {
        ValidateVolume(command.Volume);
        return ExecuteAudioPreferenceAsync(
            cancellationToken,
            (call, token) => _sipRuntime.SetInputVolumeAsync(call, command.Volume, token),
            token => _sipRuntime.SetInputVolumePreferenceAsync(command.Volume, token));
    }

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

        _work.Writer.TryComplete();
        _lifetime.Cancel();
        try
        {
            await _processor.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
        try
        {
            await _signalPump.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
        }
        await _sipRuntime.DisposeAsync();
        _lifetime.Dispose();
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _eventStore.RecoverExecutingCommandsAsync(_clock.UtcNow, cancellationToken);
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
                45_000,
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
        (string? collectionId, string? collectionBindingId) = ValidateCollectionBinding(command);
        if (_call is { State: not CallState.Ended })
        {
            throw new AgentCommandException("call_invalid_state");
        }

        if (_registrationState is not RegistrationState.Unconfigured and not RegistrationState.Unregistered)
        {
            await _sipRuntime.StopRegistrationAsync(++_registrationGeneration, cancellationToken);
        }

        _registrationRequested = false;
        _registrationRetryAttempt = 0;
        _registrationGeneration++;
        await _sipRuntime.ConfigureAsync(configuration, cancellationToken);
        _collectionId = collectionId;
        _collectionBindingId = collectionBindingId;
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
        long generation = ++_registrationGeneration;
        await _sipRuntime.StopRegistrationAsync(generation, cancellationToken);
        _registrationState = RegistrationState.Unconfigured;
        _collectionId = null;
        _collectionBindingId = null;
        await PublishRealtimeAsync(
            "registration.state_changed",
            new { state = "unconfigured", reason = "stopped" },
            cancellationToken);
    }

    private async Task StartCallCoreAsync(CallStartCommand command, CancellationToken cancellationToken)
    {
        ProtocolCodec.ValidateUuid(command.CallId, "callId");
        bool hasLegacyContext = !string.IsNullOrWhiteSpace(command.ContextToken) && command.ContextToken.Length <= 2048;
        bool hasCallContext = IsObjectId(command.CallContextId, "phonectx_");
        bool hasBinding = _collectionId != null && _collectionBindingId != null;
        if (string.IsNullOrWhiteSpace(command.Destination) || command.Destination.Length > 128 ||
            (!hasLegacyContext && !hasCallContext) ||
            command.ContextToken?.Length > 2048 ||
            command.CallContextId is not null && !hasCallContext)
        {
            throw new AgentCommandException("invalid_message");
        }

        if (hasCallContext && !hasBinding)
        {
            throw new AgentCommandException("collection_binding_required");
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

        string destination = RemotePartyNormalizer.Normalize(command.Destination);
        if (destination == "unknown")
        {
            throw new AgentCommandException("invalid_message");
        }

        _call = CallReducer.CreateOutbound(
            command.CallId,
            command.CommandId,
            _clock.UtcNow,
            destination,
            _collectionId,
            _collectionBindingId,
            command.CallContextId);
        try
        {
            await EmitCallEventAsync(
                "call.created",
                _call,
                new { direction = "outbound", remoteParty = _call.RemoteParty },
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
        SipCallHandle callHandle = NewCallHandle(command.CallId);
        _activeSipCall = callHandle;
        _ = RunOutboundCallAsync(callHandle, destination);
    }

    private async Task<CallRuntimeOperation> PrepareAnswerCoreAsync(
        CallCommand command,
        CancellationToken cancellationToken)
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
        return new CallRuntimeOperation(call, RequireActiveSipCall(call.CallId));
    }

    private async Task<CallRuntimeOperation> PrepareEndingCoreAsync(
        CallCommand command,
        CallDirection? direction,
        string reason,
        CancellationToken cancellationToken)
    {
        CallSessionState call = EnsureCurrentCall(command.CallId, direction);
        if (direction == CallDirection.Inbound && call.State != CallState.Incoming)
        {
            throw new AgentCommandException("call_invalid_state");
        }

        await ApplyCallSignalCoreAsync(
            new CallSignal(CallSignalType.Ending, _clock.UtcNow),
            new { reason },
            cancellationToken);
        return new CallRuntimeOperation(call, RequireActiveSipCall(call.CallId));
    }

    private Task<CallRuntimeOperation> PrepareDtmfCoreAsync(
        DtmfCommand command,
        CancellationToken cancellationToken)
    {
        CallSessionState call = EnsureCurrentCall(command.CallId);
        if (call.State != CallState.Connected ||
            string.IsNullOrEmpty(command.Digit) ||
            command.Digit.Length != 1 ||
            !IsValidDtmf(command.Digit[0]))
        {
            throw new AgentCommandException("call_invalid_state");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CallRuntimeOperation(call, RequireActiveSipCall(call.CallId)));
    }

    private Task<CallRuntimeOperation> PrepareAudioControlCoreAsync(
        string callId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallSessionState call = EnsureConnectedCall(callId);
        return Task.FromResult(new CallRuntimeOperation(call, RequireActiveSipCall(call.CallId)));
    }

    private async Task<AgentSnapshotPayload> GetSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ActiveCallSnapshot> calls = _call is { State: not CallState.Ended } call
            ? [new ActiveCallSnapshot(call.CallId, ToWire(call.Direction), ToWire(call.State), call.StartedAtUtc, call.AnsweredAtUtc)]
            : [];
        EventStoreHealth health;
        try
        {
            health = await _eventStore.GetHealthAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await MarkDegradedCoreAsync("outbox_unavailable", CancellationToken.None);
            health = new EventStoreHealth(0, 0, EventStoreCapacityState.Full);
        }

        long? oldestPendingAgeSeconds = health.OldestPendingAtUtc.HasValue
            ? Math.Max(0, (long)(_clock.UtcNow - health.OldestPendingAtUtc.Value).TotalSeconds)
            : null;

        return new AgentSnapshotPayload(
            _agentState,
            ToWire(_registrationState),
            _sipRuntime.AudioState,
            calls,
            health.PendingEventCount,
            _eventStore.LastSequence,
            _agentStateCode,
            _eventStore.LastAcknowledgedSequence,
            health.CapacityState.ToString().ToLowerInvariant(),
            health.StorageBytes,
            oldestPendingAgeSeconds,
            new AudioControlsSnapshot(
                _sipRuntime.IsMicrophoneMuted,
                _sipRuntime.OutputVolume,
                _sipRuntime.InputVolume));
    }

    private Task PublishAudioControlsChangedAsync(
        CallSessionState? call,
        CancellationToken cancellationToken) =>
        PublishRealtimeAsync(
            "audio.controls_changed",
            new
            {
                callId = call?.CallId,
                microphoneMuted = _sipRuntime.IsMicrophoneMuted,
                outputVolume = _sipRuntime.OutputVolume,
                inputVolume = _sipRuntime.InputVolume
            },
            cancellationToken);

    private async Task ExecuteRuntimeOperationAsync(
        CallRuntimeOperation operation,
        Func<CancellationToken, Task> action,
        TimeSpan timeout)
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operationTimeout.CancelAfter(timeout);
        try
        {
            await action(operationTimeout.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            await TryEndFailedOperationAsync(operation.Handle, "sip_operation_timeout");
            throw new AgentCommandException("sip_operation_timeout");
        }
        catch (Exception exception) when (exception is not AgentCommandException)
        {
            await TryEndFailedOperationAsync(operation.Handle, "sip_runtime_error");
            throw new AgentCommandException("sip_runtime_error");
        }
    }

    private async Task ExecuteAudioControlAsync(
        string callId,
        CancellationToken requestCancellationToken,
        Func<SipCallHandle, CancellationToken, Task> action)
    {
        CallRuntimeOperation operation = await EnqueueAsync(
            token => PrepareAudioControlCoreAsync(callId, token),
            requestCancellationToken);
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operationTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await action(operation.Handle, operationTimeout.Token);
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            throw new AgentCommandException("sip_operation_timeout");
        }
        catch (Exception exception) when (exception is not AgentCommandException)
        {
            throw new AgentCommandException("audio_control_failed");
        }

        await EnqueueAsync(
            token => PublishAudioControlsForHandleCoreAsync(operation.Handle, token),
            CancellationToken.None);
    }

    private async Task ExecuteAudioPreferenceAsync(
        CancellationToken requestCancellationToken,
        Func<SipCallHandle, CancellationToken, Task> activeCallAction,
        Func<CancellationToken, Task> idleAction)
    {
        CallRuntimeOperation? operation = await EnqueueAsync(
            PrepareOptionalAudioControlCoreAsync,
            requestCancellationToken);
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operationTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (operation is null)
            {
                await idleAction(operationTimeout.Token);
            }
            else
            {
                await activeCallAction(operation.Handle, operationTimeout.Token);
            }
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            throw new AgentCommandException("sip_operation_timeout");
        }
        catch (Exception exception) when (exception is not AgentCommandException)
        {
            throw new AgentCommandException("audio_control_failed");
        }

        await EnqueueAsync(
            token => PublishAudioControlsForOptionalHandleCoreAsync(operation?.Handle, token),
            CancellationToken.None);
    }

    private Task<CallRuntimeOperation?> PrepareOptionalAudioControlCoreAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_call is not { State: CallState.Connected } call)
        {
            return Task.FromResult<CallRuntimeOperation?>(null);
        }

        return Task.FromResult<CallRuntimeOperation?>(
            new CallRuntimeOperation(call, RequireActiveSipCall(call.CallId)));
    }

    private async Task TryEndFailedOperationAsync(SipCallHandle call, string reason)
    {
        try
        {
            await EnqueueAsync(
                token => EndCallForHandleCoreAsync(call, CallOutcome.Failed, reason, token),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is ChannelClosedException or OperationCanceledException)
        {
        }
    }

    private Task PublishAudioControlsForHandleCoreAsync(
        SipCallHandle handle,
        CancellationToken cancellationToken) =>
        IsActiveHandle(handle) && _call is { State: CallState.Connected } call
            ? PublishAudioControlsChangedAsync(call, cancellationToken)
            : Task.CompletedTask;

    private Task PublishAudioControlsForOptionalHandleCoreAsync(
        SipCallHandle? handle,
        CancellationToken cancellationToken)
    {
        if (handle is not null)
        {
            return PublishAudioControlsForHandleCoreAsync(handle, cancellationToken);
        }

        return PublishAudioControlsChangedAsync(null, cancellationToken);
    }

    private Task EmitForHandleCoreAsync(
        SipCallHandle handle,
        string eventType,
        object data,
        CancellationToken cancellationToken) =>
        IsActiveHandle(handle) && _call is { State: not CallState.Ended } call
            ? EmitCallEventAsync(eventType, call, data, cancellationToken)
            : Task.CompletedTask;

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
                await _sipRuntime.HangupAsync(RequireActiveSipCall(active.CallId), cancellationToken);
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

    private async Task RunOutboundCallAsync(SipCallHandle call, string destination)
    {
        try
        {
            await _sipRuntime.StartCallAsync(call, destination, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            try
            {
                await EnqueueAsync(
                    token => EndCallForHandleCoreAsync(call, CallOutcome.Failed, "sip_runtime_error", token),
                    CancellationToken.None);
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    private async Task PumpSipSignalsAsync()
    {
        try
        {
            await foreach (SipSignal signal in _sipRuntime.Signals.ReadAllAsync(_lifetime.Token))
            {
                try
                {
                    await EnqueueAsync(
                        token => ApplySipSignalCoreAsync(signal, token),
                        _lifetime.Token);
                    signal.ProcessingCompletion?.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    signal.ProcessingCompletion?.TrySetException(exception);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private async Task ApplySipSignalCoreAsync(SipSignal signal, CancellationToken cancellationToken)
    {
        if (IsCallScopedSignal(signal.Type) && !IsCurrentCallSignal(signal))
        {
            return;
        }

        switch (signal.Type)
        {
            case SipSignalType.RegistrationRegistering:
            case SipSignalType.RegistrationRegistered:
            case SipSignalType.RegistrationUnregistered:
            case SipSignalType.RegistrationFailed:
                if (signal.RegistrationGeneration.HasValue &&
                    signal.RegistrationGeneration.Value != _registrationGeneration)
                {
                    return;
                }
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
                await StartIncomingCallCoreAsync(signal, cancellationToken);
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
            case SipSignalType.RuntimeFailed:
                _agentState = "degraded";
                _agentStateCode = signal.SafeCode ?? "sip_runtime_failed";
                _registrationRequested = false;
                _registrationState = RegistrationState.Failed;
                if (_call is { State: not CallState.Ended })
                {
                    await EndCallCoreAsync(CallOutcome.Failed, _agentStateCode, cancellationToken);
                }
                await PublishRealtimeAsync(
                    "agent.state_changed",
                    new { state = _agentState, code = _agentStateCode },
                    cancellationToken);
                break;
        }
    }

    private async Task StartIncomingCallCoreAsync(SipSignal signal, CancellationToken cancellationToken)
    {
        SipCallHandle incomingHandle = signal.Call
            ?? throw new AgentCommandException("sip_runtime_error");
        if (_call is { State: not CallState.Ended })
        {
            await _sipRuntime.RejectAsync(incomingHandle, cancellationToken);
            return;
        }

        if (_collectionId is null || _collectionBindingId is null)
        {
            await _sipRuntime.RejectUnavailableAsync(incomingHandle, cancellationToken);
            return;
        }

        try
        {
            await EnsureCapacityForNewCallAsync(cancellationToken);
        }
        catch (AgentCommandException exception) when (IsCapacityCode(exception.ErrorCode))
        {
            await _sipRuntime.RejectUnavailableAsync(incomingHandle, cancellationToken);
            if (_registrationState != RegistrationState.Unconfigured)
            {
                await StopRegistrationCoreAsync(cancellationToken);
            }

            return;
        }

        _call = CallReducer.CreateInbound(
            _ids.NewId(),
            _clock.UtcNow,
            RemotePartyNormalizer.Normalize(signal.Caller),
            _collectionId,
            _collectionBindingId);
        _activeSipCall = incomingHandle with { PublicCallId = _call.CallId };
        try
        {
            await EmitCallEventAsync(
                "call.created",
                _call,
                new { direction = "inbound", remoteParty = _call.RemoteParty },
                cancellationToken);
            await ApplyCallSignalCoreAsync(
                new CallSignal(CallSignalType.Incoming, _clock.UtcNow),
                new { direction = "inbound" },
                cancellationToken);
        }
        catch (AgentCommandException exception) when (exception.ErrorCode == "outbox_unavailable")
        {
            await _sipRuntime.RejectUnavailableAsync(incomingHandle, cancellationToken);
            _call = null;
            _activeSipCall = null;
            return;
        }

        if (!_portalConnected)
        {
            await ApplyCallSignalCoreAsync(
                new CallSignal(CallSignalType.Ending, _clock.UtcNow),
                new { reason = "portal_unavailable" },
                cancellationToken);
            await _sipRuntime.RejectUnavailableAsync(RequireActiveSipCall(_call.CallId), cancellationToken);
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
        _activeSipCall = null;
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

    private Task EndCallForHandleCoreAsync(
        SipCallHandle call,
        CallOutcome outcome,
        string reason,
        CancellationToken cancellationToken) =>
        _activeSipCall != null &&
        _activeSipCall.Generation == call.Generation &&
        string.Equals(_activeSipCall.RuntimeCallId, call.RuntimeCallId, StringComparison.Ordinal)
            ? EndCallCoreAsync(outcome, reason, cancellationToken)
            : Task.CompletedTask;

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
                    JsonSerializer.Serialize(data, ProtocolJson.Options),
                    call.CollectionId,
                    call.CollectionBindingId,
                    call.CallContextId),
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
            await _sipRuntime.StartRegistrationAsync(generation, cancellationToken);
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
            AgentPerformanceTelemetry.RecordCoordinatorQueueWait(
                Stopwatch.GetElapsedTime(item.EnqueuedTimestamp));
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
            new CoordinatorWorkItem<T>(
                action,
                completion,
                cancellationToken,
                cancelExecution,
                Stopwatch.GetTimestamp()),
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

    private SipCallHandle NewCallHandle(string publicCallId) =>
        new(_ids.NewId(), ++_callGeneration, publicCallId);

    private SipCallHandle RequireActiveSipCall(string publicCallId)
    {
        SipCallHandle? call = _activeSipCall;
        if (call == null ||
            call.PublicCallId != null && !string.Equals(call.PublicCallId, publicCallId, StringComparison.Ordinal))
        {
            throw new AgentCommandException("call_not_found");
        }

        return call;
    }

    private bool IsCurrentCallSignal(SipSignal signal)
    {
        return signal.Call != null && IsActiveHandle(signal.Call);
    }

    private bool IsActiveHandle(SipCallHandle handle) =>
        _activeSipCall != null &&
        _activeSipCall.Generation == handle.Generation &&
        string.Equals(_activeSipCall.RuntimeCallId, handle.RuntimeCallId, StringComparison.Ordinal);

    private static bool IsCallScopedSignal(SipSignalType type) => type is
        SipSignalType.CallTrying or
        SipSignalType.CallRinging or
        SipSignalType.CallConnected or
        SipSignalType.CallFailed or
        SipSignalType.CallRemoteEnded or
        SipSignalType.IncomingCancelled or
        SipSignalType.MediaReady or
        SipSignalType.MediaDegraded or
        SipSignalType.DtmfReceived;

    private CallSessionState EnsureConnectedCall(string callId)
    {
        CallSessionState call = EnsureCurrentCall(callId);
        if (call.State != CallState.Connected)
        {
            throw new AgentCommandException("call_invalid_state");
        }

        return call;
    }

    private static void ValidateVolume(int volume)
    {
        if (volume is < 0 or > 100)
        {
            throw new AgentCommandException("invalid_message");
        }
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

    private static (string? CollectionId, string? CollectionBindingId) ValidateCollectionBinding(
        ConfigureCommand command)
    {
        bool hasCollection = !string.IsNullOrWhiteSpace(command.CollectionId);
        bool hasBinding = !string.IsNullOrWhiteSpace(command.CollectionBindingId);
        if (!hasCollection && !hasBinding)
        {
            return (null, null);
        }

        if (!hasCollection || !hasBinding ||
            command.CollectionId!.Length > 60 || command.CollectionId != command.CollectionId.Trim() ||
            command.CollectionId.Any(char.IsControl) ||
            !IsObjectId(command.CollectionBindingId, "phonebind_"))
        {
            throw new AgentCommandException("invalid_message");
        }

        return (command.CollectionId, command.CollectionBindingId);
    }

    private static bool IsObjectId(string? value, string prefix) =>
        value is not null && value.StartsWith(prefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(value[prefix.Length..], "N", out _);

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

    private abstract record CoordinatorWorkItem(long EnqueuedTimestamp)
    {
        public abstract Task ExecuteAsync(CancellationToken lifetimeToken);
    }

    private sealed record CallRuntimeOperation(CallSessionState Call, SipCallHandle Handle);

    private sealed record CoordinatorWorkItem<T>(
        Func<CancellationToken, Task<T>> Action,
        TaskCompletionSource<T> Completion,
        CancellationToken RequestCancellationToken,
        bool CancelExecution,
        long EnqueuedTimestamp) : CoordinatorWorkItem(EnqueuedTimestamp)
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
