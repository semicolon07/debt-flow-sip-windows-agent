using System.Text.Json;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Host;

public sealed class V1CommandDispatcher(
    AgentCoordinator coordinator,
    IAgentEventStore eventStore,
    IAgentClock clock)
{
    private static readonly HashSet<string> CallScopedCommandTypes =
        ["call.start", "call.answer", "call.reject", "call.hangup", "call.dtmf"];

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
        try
        {
            commandId = GetCommandId(envelope);
            ProtocolCodec.ValidateUuid(commandId, "commandId");
            callId = GetCallIdForFingerprint(envelope);
        }
        catch (ProtocolException exception)
        {
            return Error(envelope.MessageId, exception.Code, false);
        }

        string requestHash = ProtocolCodec.ComputeCommandIdentityHash(envelope.Type, commandId, callId);
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
            if (!string.Equals(existing.CommandType, envelope.Type, StringComparison.Ordinal) ||
                !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return Result(commandId, envelope.Type, false, "command_duplicate_conflict");
            }

            return System.Text.Encoding.UTF8.GetBytes(existing.ResultJson);
        }

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
