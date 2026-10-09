using System.Text.Json;
using System.Net;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed record CommandMaintenancePolicy(
    TimeSpan Retention,
    int MaximumRetained,
    TimeSpan Interval,
    TimeSpan FailureRetry)
{
    public static CommandMaintenancePolicy Default { get; } = new(
        TimeSpan.FromDays(30),
        45_000,
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(1));
}

public sealed class V1CommandDispatcher(
    AgentCoordinator coordinator,
    IAgentEventStore eventStore,
    IAgentClock clock,
    ICommandFingerprintService fingerprintService,
    CommandMaintenancePolicy? maintenancePolicy = null)
{
    private long _nextPruneUtcTicks;
    private int _fingerprintKeyState;
    private readonly BackgroundTaskSupervisor _maintenanceTasks = new();
    private readonly CommandMaintenancePolicy _maintenancePolicy = ValidateMaintenancePolicy(
        maintenancePolicy ?? CommandMaintenancePolicy.Default);

    public async Task<byte[]> DispatchAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Kind == "ack" && envelope.Type == "events.ack")
        {
            AckPayload ack = ProtocolCodec.DeserializePayload<AckPayload>(envelope.Payload);
            ProtocolCodec.ValidateUuid(ack.AgentInstanceId, "agentInstanceId");
            if (ack.AcknowledgedThroughSequence < 0 ||
                !string.Equals(ack.AgentInstanceId, eventStore.AgentInstanceId, StringComparison.Ordinal))
            {
                return Error(envelope.MessageId, "invalid_message", false);
            }

            try
            {
                await eventStore.AcknowledgeThroughAsync(ack.AcknowledgedThroughSequence, cancellationToken);
                await coordinator.RefreshStorageHealthAsync(cancellationToken);
                return ProtocolCodec.Serialize(
                    "command_result",
                    "events.ack.result",
                    new { accepted = true, acknowledgedThroughSequence = ack.AcknowledgedThroughSequence });
            }
            catch (InvalidOperationException exception) when (
                exception.Message is "ack_sequence_not_emitted" or "ack_sequence_gap")
            {
                return Error(envelope.MessageId, exception.Message, false);
            }
            catch (AgentStoreException exception)
            {
                await coordinator.MarkDegradedAsync(exception.ErrorCode, CancellationToken.None);
                return Error(envelope.MessageId, exception.ErrorCode, true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await coordinator.MarkDegradedAsync("outbox_unavailable", CancellationToken.None);
                return Error(envelope.MessageId, "outbox_unavailable", true);
            }
        }

        if (envelope.Kind != "command")
        {
            return Error(envelope.MessageId, "invalid_message", false);
        }

        PreparedCommand command;
        try
        {
            command = PrepareCommand(envelope);
        }
        catch (ProtocolException exception)
        {
            return Error(envelope.MessageId, exception.Code, false);
        }

        string requestHash;
        try
        {
            CommandFingerprint fingerprint = fingerprintService.Compute(command.CanonicalPayload);
            if (fingerprint.KeyWasCreated)
            {
                Volatile.Write(ref _fingerprintKeyState, 2);
                bool existingV2Commands = await eventStore.HasCommandRequestHashPrefixAsync("v2:", cancellationToken);
                Volatile.Write(ref _fingerprintKeyState, existingV2Commands ? 2 : 1);
            }
            if (Volatile.Read(ref _fingerprintKeyState) == 2)
                return Result(command.CommandId, envelope.Type, false, "command_key_unavailable");

            requestHash = fingerprint.Value;
        }
        catch (AgentConfigurationException exception)
        {
            return Result(command.CommandId, envelope.Type, false, exception.Code);
        }
        catch (AgentStoreException exception)
        {
            await coordinator.MarkDegradedAsync(exception.ErrorCode, CancellationToken.None);
            return Result(command.CommandId, envelope.Type, false, exception.ErrorCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(command.CommandId, envelope.Type, false, "command_key_unavailable");
        }
        ProcessedCommand? existing;
        try
        {
            existing = await eventStore.FindCommandAsync(command.CommandId, cancellationToken);
        }
        catch (AgentStoreException exception)
        {
            await coordinator.MarkDegradedAsync(exception.ErrorCode, CancellationToken.None);
            return Result(command.CommandId, envelope.Type, false, exception.ErrorCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await coordinator.MarkDegradedAsync("outbox_unavailable", CancellationToken.None);
            return Result(command.CommandId, envelope.Type, false, "outbox_unavailable");
        }
        if (existing != null)
        {
            string expectedHash = existing.RequestHash.StartsWith("v2:", StringComparison.Ordinal)
                ? requestHash
                : ProtocolCodec.ComputeCommandIdentityHash(envelope.Type, command.CommandId, command.CallId);
            if (!string.Equals(existing.CommandType, envelope.Type, StringComparison.Ordinal) ||
                !string.Equals(existing.RequestHash, expectedHash, StringComparison.Ordinal))
            {
                return Result(command.CommandId, envelope.Type, false, "command_duplicate_conflict");
            }

            return System.Text.Encoding.UTF8.GetBytes(existing.ResultJson);
        }

        byte[] provisional = Result(command.CommandId, envelope.Type, false, "command_outcome_unknown");
        try
        {
            await eventStore.SaveCommandAsync(
                new ProcessedCommand(
                    command.CommandId,
                    envelope.Type,
                    requestHash,
                    System.Text.Encoding.UTF8.GetString(provisional),
                    clock.UtcNow,
                    "executing"),
                cancellationToken);
        }
        catch (AgentStoreException exception)
        {
            await coordinator.MarkDegradedAsync(exception.ErrorCode, CancellationToken.None);
            return Result(command.CommandId, envelope.Type, false, exception.ErrorCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await coordinator.MarkDegradedAsync("outbox_unavailable", CancellationToken.None);
            return Result(command.CommandId, envelope.Type, false, "outbox_unavailable");
        }

        byte[] result;
        string executionState;
        try
        {
            await command.Execute(cancellationToken);
            result = Result(command.CommandId, envelope.Type, true, null);
            executionState = "completed";
        }
        catch (AgentCommandException exception)
        {
            result = Result(command.CommandId, envelope.Type, false, exception.ErrorCode);
            executionState = "failed";
        }
        catch (ProtocolException exception)
        {
            result = Result(command.CommandId, envelope.Type, false, exception.Code);
            executionState = "failed";
        }
        catch
        {
            result = Result(command.CommandId, envelope.Type, false, "internal_error");
            executionState = "failed";
        }

        try
        {
            await eventStore.SaveCommandAsync(
                new ProcessedCommand(
                    command.CommandId,
                    envelope.Type,
                    requestHash,
                    System.Text.Encoding.UTF8.GetString(result),
                    clock.UtcNow,
                    executionState),
                cancellationToken);
        }
        catch (AgentStoreException exception)
        {
            await coordinator.MarkDegradedAsync(exception.ErrorCode, CancellationToken.None);
            return Result(command.CommandId, envelope.Type, false, "command_outcome_unknown");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await coordinator.MarkDegradedAsync("outbox_unavailable", CancellationToken.None);
            return Result(command.CommandId, envelope.Type, false, "command_outcome_unknown");
        }

        ScheduleCommandPruneIfDue();
        return result;
    }

    private PreparedCommand PrepareCommand(ProtocolEnvelope envelope)
    {
        PreparedCommand prepared = envelope.Type switch
        {
            "session.configure" => PrepareConfigure(envelope),
            "registration.start" => PrepareHeader(
                envelope,
                coordinator.StartRegistrationAsync),
            "registration.stop" => PrepareHeader(
                envelope,
                coordinator.StopRegistrationAsync),
            "call.start" => PrepareCallStart(envelope),
            "call.answer" => PrepareCall(
                envelope,
                coordinator.AnswerAsync),
            "call.reject" => PrepareCall(
                envelope,
                coordinator.RejectAsync),
            "call.hangup" => PrepareCall(
                envelope,
                coordinator.HangupAsync),
            "call.dtmf" => PrepareDtmf(envelope),
            "call.mute.set" => PrepareCallMute(envelope),
            "audio.output.volume.set" => PrepareCallVolume(
                envelope,
                coordinator.SetOutputVolumeAsync),
            "audio.input.volume.set" => PrepareCallVolume(
                envelope,
                coordinator.SetInputVolumeAsync),
            "audio.output.volume.preference.set" => PrepareVolumePreference(
                envelope,
                coordinator.SetOutputVolumePreferenceAsync),
            "audio.input.volume.preference.set" => PrepareVolumePreference(
                envelope,
                coordinator.SetInputVolumePreferenceAsync),
            "audio.devices.preference.set" => PrepareAudioDevicePreference(envelope),
            "audio.output.test" => PrepareAudioDeviceTest(
                envelope,
                coordinator.TestOutputDeviceAsync),
            "audio.input.test" => PrepareAudioDeviceTest(
                envelope,
                coordinator.TestInputDeviceAsync),
            "state.get" => PrepareHeader(envelope, static _ => Task.CompletedTask),
            _ => throw new ProtocolException("invalid_message", "Command type is unsupported.")
        };

        ProtocolCodec.ValidateUuid(prepared.CommandId, "commandId");
        if (prepared.CallId != null)
        {
            ProtocolCodec.ValidateUuid(prepared.CallId, "callId");
        }

        return prepared;
    }

    private PreparedCommand PrepareConfigure(ProtocolEnvelope envelope)
    {
        ConfigureCommand command = ProtocolCodec.DeserializePayload<ConfigureCommand>(envelope.Payload);
        object canonical = CanonicalizeConfigure(command);
        return CreatePrepared(command.CommandId, null, canonical, token => coordinator.ConfigureAsync(command, token));
    }

    private PreparedCommand PrepareHeader(
        ProtocolEnvelope envelope,
        Func<CancellationToken, Task> execute)
    {
        CommandHeader command = ProtocolCodec.DeserializePayload<CommandHeader>(envelope.Payload);
        return CreatePrepared(command.CommandId, null, command, execute);
    }

    private PreparedCommand PrepareCallStart(ProtocolEnvelope envelope)
    {
        CallStartCommand command = ProtocolCodec.DeserializePayload<CallStartCommand>(envelope.Payload);
        object canonical = CanonicalizeCallStart(command);
        return CreatePrepared(
            command.CommandId,
            command.CallId,
            canonical,
            token => coordinator.StartCallAsync(command, token));
    }

    private PreparedCommand PrepareCall(
        ProtocolEnvelope envelope,
        Func<CallCommand, CancellationToken, Task> execute)
    {
        CallCommand command = ProtocolCodec.DeserializePayload<CallCommand>(envelope.Payload);
        return CreatePrepared(command.CommandId, command.CallId, command, token => execute(command, token));
    }

    private PreparedCommand PrepareDtmf(ProtocolEnvelope envelope)
    {
        DtmfCommand command = ProtocolCodec.DeserializePayload<DtmfCommand>(envelope.Payload);
        object canonical = CanonicalizeDtmf(command);
        return CreatePrepared(
            command.CommandId,
            command.CallId,
            canonical,
            token => coordinator.SendDtmfAsync(command, token));
    }

    private PreparedCommand PrepareCallMute(ProtocolEnvelope envelope)
    {
        CallMuteCommand command = ProtocolCodec.DeserializePayload<CallMuteCommand>(envelope.Payload);
        return CreatePrepared(
            command.CommandId,
            command.CallId,
            command,
            token => coordinator.SetMicrophoneMutedAsync(command, token));
    }

    private PreparedCommand PrepareCallVolume(
        ProtocolEnvelope envelope,
        Func<CallVolumeCommand, CancellationToken, Task> execute)
    {
        CallVolumeCommand command = ProtocolCodec.DeserializePayload<CallVolumeCommand>(envelope.Payload);
        CallVolumeCommand canonical = CanonicalizeVolume(command);
        return CreatePrepared(command.CommandId, command.CallId, canonical, token => execute(command, token));
    }

    private PreparedCommand PrepareVolumePreference(
        ProtocolEnvelope envelope,
        Func<AudioVolumePreferenceCommand, CancellationToken, Task> execute)
    {
        AudioVolumePreferenceCommand command =
            ProtocolCodec.DeserializePayload<AudioVolumePreferenceCommand>(envelope.Payload);
        AudioVolumePreferenceCommand canonical = CanonicalizeVolumePreference(command);
        return CreatePrepared(command.CommandId, null, canonical, token => execute(command, token));
    }

    private PreparedCommand PrepareAudioDevicePreference(ProtocolEnvelope envelope)
    {
        AudioDevicePreferenceCommand command =
            ProtocolCodec.DeserializePayload<AudioDevicePreferenceCommand>(envelope.Payload);
        AudioDevicePreferenceCommand canonical = CanonicalizeAudioDevicePreference(command);
        return CreatePrepared(
            command.CommandId,
            null,
            canonical,
            token => coordinator.SetAudioDevicePreferencesAsync(command, token));
    }

    private PreparedCommand PrepareAudioDeviceTest(
        ProtocolEnvelope envelope,
        Func<AudioDeviceTestCommand, CancellationToken, Task> execute)
    {
        AudioDeviceTestCommand command = ProtocolCodec.DeserializePayload<AudioDeviceTestCommand>(envelope.Payload);
        AudioDeviceTestCommand canonical = CanonicalizeAudioDeviceTest(command);
        return CreatePrepared(command.CommandId, null, canonical, token => execute(command, token));
    }

    private static PreparedCommand CreatePrepared(
        string commandId,
        string? callId,
        object canonical,
        Func<CancellationToken, Task> execute) => new(
        commandId,
        callId,
        JsonSerializer.SerializeToUtf8Bytes(canonical, canonical.GetType(), ProtocolJson.Options),
        execute);

    private static object CanonicalizeConfigure(ConfigureCommand command)
    {
        string host = command.Host.Trim();
        string username = command.Username.Trim();
        if (!IPAddress.TryParse(host, out IPAddress? address) ||
            !string.Equals(host, address.ToString(), StringComparison.OrdinalIgnoreCase) ||
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0 ||
            command.Port is < 1 or > 65535 ||
            username.Length is 0 or > 128 ||
            command.Password.Length is 0 or > 512)
        {
            throw new ProtocolException("invalid_message", "Configuration payload is invalid.");
        }

        return new
        {
            commandId = command.CommandId,
            host = address.ToString(),
            command.Port,
            username,
            command.Password,
            command.CollectionId,
            command.CollectionBindingId
        };
    }

    private static object CanonicalizeCallStart(CallStartCommand command)
    {
        string destination = RemotePartyNormalizer.Normalize(command.Destination);
        bool hasLegacyContext = !string.IsNullOrWhiteSpace(command.ContextToken) && command.ContextToken.Length <= 2048;
        bool hasCallContext = command.CallContextId is not null &&
                              command.CallContextId.StartsWith("phonectx_", StringComparison.Ordinal) &&
                              Guid.TryParseExact(command.CallContextId[9..], "N", out _);
        if (destination == "unknown" ||
            (!hasLegacyContext && !hasCallContext) ||
            command.ContextToken?.Length > 2048 ||
            command.CallContextId is not null && !hasCallContext)
        {
            throw new ProtocolException("invalid_message", "Destination is invalid.");
        }

        return new
        {
            commandId = command.CommandId,
            callId = command.CallId,
            destination,
            command.ContextToken,
            command.CallContextId
        };
    }

    private static object CanonicalizeDtmf(DtmfCommand command)
    {
        if (command.Digit.Length != 1 ||
            command.Digit[0] is not (>= '0' and <= '9' or '*' or '#' or >= 'A' and <= 'D' or >= 'a' and <= 'd'))
        {
            throw new ProtocolException("invalid_message", "DTMF digit is invalid.");
        }

        return new
        {
            commandId = command.CommandId,
            callId = command.CallId,
            digit = command.Digit.ToUpperInvariant()
        };
    }

    private static CallVolumeCommand CanonicalizeVolume(CallVolumeCommand command)
    {
        if (command.Volume is < 0 or > 100)
        {
            throw new ProtocolException("invalid_message", "Volume is invalid.");
        }

        return command;
    }

    private static AudioVolumePreferenceCommand CanonicalizeVolumePreference(
        AudioVolumePreferenceCommand command)
    {
        if (command.Volume is < 0 or > 100)
        {
            throw new ProtocolException("invalid_message", "Volume is invalid.");
        }

        return command;
    }

    private static AudioDevicePreferenceCommand CanonicalizeAudioDevicePreference(
        AudioDevicePreferenceCommand command)
    {
        ValidateDeviceId(command.OutputDeviceId);
        ValidateDeviceId(command.InputDeviceId);
        return command;
    }

    private static AudioDeviceTestCommand CanonicalizeAudioDeviceTest(AudioDeviceTestCommand command)
    {
        ValidateDeviceId(command.DeviceId);
        return command;
    }

    private static void ValidateDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            deviceId.Length > 128 ||
            deviceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ProtocolException("invalid_message", "Audio device id is invalid.");
        }
    }

    private void ScheduleCommandPruneIfDue()
    {
        long nowTicks = clock.UtcNow.UtcTicks;
        long nextTicks = Volatile.Read(ref _nextPruneUtcTicks);
        if (nowTicks < nextTicks ||
            Interlocked.CompareExchange(
                ref _nextPruneUtcTicks,
                clock.UtcNow.Add(_maintenancePolicy.Interval).UtcTicks,
                nextTicks) != nextTicks)
        {
            return;
        }

        _maintenanceTasks.Track("command_prune", PruneCommandsAsync());
    }

    private async Task PruneCommandsAsync()
    {
        try
        {
            await eventStore.PruneCommandsAsync(
                clock.UtcNow.Subtract(_maintenancePolicy.Retention),
                _maintenancePolicy.MaximumRetained,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AgentPerformanceTelemetry.RecordCommandPruneFailure();
            Volatile.Write(ref _nextPruneUtcTicks, clock.UtcNow.Add(_maintenancePolicy.FailureRetry).UtcTicks);
        }
    }

    private sealed record PreparedCommand(
        string CommandId,
        string? CallId,
        byte[] CanonicalPayload,
        Func<CancellationToken, Task> Execute);

    private static CommandMaintenancePolicy ValidateMaintenancePolicy(CommandMaintenancePolicy policy)
    {
        if (policy.Retention <= TimeSpan.Zero ||
            policy.MaximumRetained < 1 ||
            policy.Interval <= TimeSpan.Zero ||
            policy.FailureRetry <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        return policy;
    }

    private static byte[] Result(string commandId, string commandType, bool accepted, string? errorCode) =>
        ProtocolCodec.Serialize(
            "command_result",
            $"{commandType}.result",
            new CommandResultPayload(commandId, accepted, errorCode));

    private static byte[] Error(string? correlationMessageId, string code, bool retryable) =>
        ProtocolCodec.Serialize(
            "error",
            "protocol.error",
            new ErrorPayload(correlationMessageId, code, retryable));
}
