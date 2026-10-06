using System.Text.Json;
using System.Net;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class V1CommandDispatcher(
    AgentCoordinator coordinator,
    IAgentEventStore eventStore,
    IAgentClock clock,
    ICommandFingerprintService fingerprintService)
{
    private long _nextPruneUtcTicks;
    private int _fingerprintKeyState;
    private static readonly HashSet<string> CallScopedCommandTypes =
        [
            "call.start",
            "call.answer",
            "call.reject",
            "call.hangup",
            "call.dtmf",
            "call.mute.set",
            "audio.output.volume.set",
            "audio.input.volume.set"
        ];

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

        string commandId;
        string? callId;
        byte[] canonicalPayload;
        try
        {
            commandId = GetCommandId(envelope);
            ProtocolCodec.ValidateUuid(commandId, "commandId");
            callId = GetCallIdForFingerprint(envelope);
            canonicalPayload = CanonicalizeCommand(envelope);
        }
        catch (ProtocolException exception)
        {
            return Error(envelope.MessageId, exception.Code, false);
        }

        string requestHash;
        try
        {
            CommandFingerprint fingerprint = fingerprintService.Compute(canonicalPayload);
            if (fingerprint.KeyWasCreated)
            {
                Volatile.Write(ref _fingerprintKeyState, 2);
                bool existingV2Commands = await eventStore.HasCommandRequestHashPrefixAsync("v2:", cancellationToken);
                Volatile.Write(ref _fingerprintKeyState, existingV2Commands ? 2 : 1);
            }
            if (Volatile.Read(ref _fingerprintKeyState) == 2)
                return Result(commandId, envelope.Type, false, "command_key_unavailable");

            requestHash = fingerprint.Value;
        }
        catch (AgentConfigurationException exception)
        {
            return Result(commandId, envelope.Type, false, exception.Code);
        }
        catch (AgentStoreException exception)
        {
            await coordinator.MarkDegradedAsync(exception.ErrorCode, CancellationToken.None);
            return Result(commandId, envelope.Type, false, exception.ErrorCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(commandId, envelope.Type, false, "command_key_unavailable");
        }
        ProcessedCommand? existing;
        try
        {
            existing = await eventStore.FindCommandAsync(commandId, cancellationToken);
        }
        catch (AgentStoreException exception)
        {
            await coordinator.MarkDegradedAsync(exception.ErrorCode, CancellationToken.None);
            return Result(commandId, envelope.Type, false, exception.ErrorCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await coordinator.MarkDegradedAsync("outbox_unavailable", CancellationToken.None);
            return Result(commandId, envelope.Type, false, "outbox_unavailable");
        }
        if (existing != null)
        {
            string expectedHash = existing.RequestHash.StartsWith("v2:", StringComparison.Ordinal)
                ? requestHash
                : ProtocolCodec.ComputeCommandIdentityHash(envelope.Type, commandId, callId);
            if (!string.Equals(existing.CommandType, envelope.Type, StringComparison.Ordinal) ||
                !string.Equals(existing.RequestHash, expectedHash, StringComparison.Ordinal))
            {
                return Result(commandId, envelope.Type, false, "command_duplicate_conflict");
            }

            return System.Text.Encoding.UTF8.GetBytes(existing.ResultJson);
        }

        await MaybePruneCommandsAsync();
        byte[] provisional = Result(commandId, envelope.Type, false, "command_outcome_unknown");
        try
        {
            await eventStore.SaveCommandAsync(
                new ProcessedCommand(
                    commandId,
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
            return Result(commandId, envelope.Type, false, exception.ErrorCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await coordinator.MarkDegradedAsync("outbox_unavailable", CancellationToken.None);
            return Result(commandId, envelope.Type, false, "outbox_unavailable");
        }

        byte[] result;
        string executionState;
        try
        {
            await ExecuteAsync(envelope, cancellationToken);
            result = Result(commandId, envelope.Type, true, null);
            executionState = "completed";
        }
        catch (AgentCommandException exception)
        {
            result = Result(commandId, envelope.Type, false, exception.ErrorCode);
            executionState = "failed";
        }
        catch (ProtocolException exception)
        {
            result = Result(commandId, envelope.Type, false, exception.Code);
            executionState = "failed";
        }
        catch
        {
            result = Result(commandId, envelope.Type, false, "internal_error");
            executionState = "failed";
        }

        try
        {
            await eventStore.SaveCommandAsync(
                new ProcessedCommand(
                    commandId,
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
            return Result(commandId, envelope.Type, false, "command_outcome_unknown");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await coordinator.MarkDegradedAsync("outbox_unavailable", CancellationToken.None);
            return Result(commandId, envelope.Type, false, "command_outcome_unknown");
        }

        await MaybePruneCommandsAsync();
        return result;
    }

    private async Task ExecuteAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Type)
        {
            case "session.configure":
                await coordinator.ConfigureAsync(
                    ProtocolCodec.DeserializePayload<ConfigureCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "registration.start":
                _ = ProtocolCodec.DeserializePayload<CommandHeader>(envelope.Payload);
                await coordinator.StartRegistrationAsync(cancellationToken);
                break;
            case "registration.stop":
                _ = ProtocolCodec.DeserializePayload<CommandHeader>(envelope.Payload);
                await coordinator.StopRegistrationAsync(cancellationToken);
                break;
            case "call.start":
                await coordinator.StartCallAsync(
                    ProtocolCodec.DeserializePayload<CallStartCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "call.answer":
                await coordinator.AnswerAsync(
                    ProtocolCodec.DeserializePayload<CallCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "call.reject":
                await coordinator.RejectAsync(
                    ProtocolCodec.DeserializePayload<CallCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "call.hangup":
                await coordinator.HangupAsync(
                    ProtocolCodec.DeserializePayload<CallCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "call.dtmf":
                await coordinator.SendDtmfAsync(
                    ProtocolCodec.DeserializePayload<DtmfCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "call.mute.set":
                await coordinator.SetMicrophoneMutedAsync(
                    ProtocolCodec.DeserializePayload<CallMuteCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "audio.output.volume.set":
                await coordinator.SetOutputVolumeAsync(
                    ProtocolCodec.DeserializePayload<CallVolumeCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "audio.input.volume.set":
                await coordinator.SetInputVolumeAsync(
                    ProtocolCodec.DeserializePayload<CallVolumeCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "audio.output.volume.preference.set":
                await coordinator.SetOutputVolumePreferenceAsync(
                    ProtocolCodec.DeserializePayload<AudioVolumePreferenceCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "audio.input.volume.preference.set":
                await coordinator.SetInputVolumePreferenceAsync(
                    ProtocolCodec.DeserializePayload<AudioVolumePreferenceCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "audio.devices.preference.set":
                await coordinator.SetAudioDevicePreferencesAsync(
                    ProtocolCodec.DeserializePayload<AudioDevicePreferenceCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "audio.output.test":
                await coordinator.TestOutputDeviceAsync(
                    ProtocolCodec.DeserializePayload<AudioDeviceTestCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "audio.input.test":
                await coordinator.TestInputDeviceAsync(
                    ProtocolCodec.DeserializePayload<AudioDeviceTestCommand>(envelope.Payload),
                    cancellationToken);
                break;
            case "state.get":
                _ = ProtocolCodec.DeserializePayload<CommandHeader>(envelope.Payload);
                break;
            default:
                throw new AgentCommandException("invalid_message");
        }
    }

    private static string GetCommandId(ProtocolEnvelope envelope)
    {
        if (!envelope.Payload.TryGetProperty("commandId", out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new ProtocolException("invalid_message", "commandId is required.");
        }

        return property.GetString()!;
    }

    private static string? GetCallIdForFingerprint(ProtocolEnvelope envelope)
    {
        if (!CallScopedCommandTypes.Contains(envelope.Type))
        {
            return null;
        }

        if (!envelope.Payload.TryGetProperty("callId", out JsonElement property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new ProtocolException("invalid_message", "callId is required.");
        }

        string callId = property.GetString()!;
        ProtocolCodec.ValidateUuid(callId, "callId");
        return callId;
    }

    private static byte[] CanonicalizeCommand(ProtocolEnvelope envelope)
    {
        object canonical = envelope.Type switch
        {
            "session.configure" => CanonicalizeConfigure(
                ProtocolCodec.DeserializePayload<ConfigureCommand>(envelope.Payload)),
            "registration.start" or "registration.stop" or "state.get" =>
                ProtocolCodec.DeserializePayload<CommandHeader>(envelope.Payload),
            "call.start" => CanonicalizeCallStart(
                ProtocolCodec.DeserializePayload<CallStartCommand>(envelope.Payload)),
            "call.answer" or "call.reject" or "call.hangup" =>
                ProtocolCodec.DeserializePayload<CallCommand>(envelope.Payload),
            "call.dtmf" => CanonicalizeDtmf(
                ProtocolCodec.DeserializePayload<DtmfCommand>(envelope.Payload)),
            "call.mute.set" => ProtocolCodec.DeserializePayload<CallMuteCommand>(envelope.Payload),
            "audio.output.volume.set" or "audio.input.volume.set" => CanonicalizeVolume(
                ProtocolCodec.DeserializePayload<CallVolumeCommand>(envelope.Payload)),
            "audio.output.volume.preference.set" or "audio.input.volume.preference.set" =>
                CanonicalizeVolumePreference(
                    ProtocolCodec.DeserializePayload<AudioVolumePreferenceCommand>(envelope.Payload)),
            "audio.devices.preference.set" => CanonicalizeAudioDevicePreference(
                ProtocolCodec.DeserializePayload<AudioDevicePreferenceCommand>(envelope.Payload)),
            "audio.output.test" or "audio.input.test" => CanonicalizeAudioDeviceTest(
                ProtocolCodec.DeserializePayload<AudioDeviceTestCommand>(envelope.Payload)),
            _ => throw new ProtocolException("invalid_message", "Command type is unsupported.")
        };

        return JsonSerializer.SerializeToUtf8Bytes(canonical, canonical.GetType(), ProtocolJson.Options);
    }

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

    private async Task MaybePruneCommandsAsync()
    {
        long nowTicks = clock.UtcNow.UtcTicks;
        long nextTicks = Volatile.Read(ref _nextPruneUtcTicks);
        if (nowTicks < nextTicks ||
            Interlocked.CompareExchange(
                ref _nextPruneUtcTicks,
                clock.UtcNow.AddMinutes(15).UtcTicks,
                nextTicks) != nextTicks)
        {
            return;
        }

        try
        {
            await eventStore.PruneCommandsAsync(
                clock.UtcNow.AddDays(-30),
                45_000,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
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
