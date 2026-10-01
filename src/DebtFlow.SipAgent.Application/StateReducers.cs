namespace DebtFlow.SipAgent.Application;

public enum RegistrationState
{
    Unconfigured,
    Unregistered,
    Registering,
    Registered,
    Unregistering,
    Retrying,
    Failed
}

public enum CallDirection
{
    Outbound,
    Inbound
}

public enum CallState
{
    Created,
    Dialing,
    Trying,
    Ringing,
    Incoming,
    Answering,
    Connected,
    Ending,
    Ended
}

public enum CallOutcome
{
    Completed,
    Busy,
    NoAnswer,
    Cancelled,
    Rejected,
    Missed,
    Failed
}

public sealed record CallSessionState(
    string CallId,
    string? CommandId,
    CallDirection Direction,
    CallState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? AnsweredAtUtc,
    DateTimeOffset? EndedAtUtc,
    CallOutcome? Outcome,
    string? EndReason,
    string MaskedRemoteParty);

public enum CallSignalType
{
    Dial,
    Trying,
    Ringing,
    Incoming,
    Answering,
    Connected,
    Ending,
    End
}

public sealed record CallSignal(
    CallSignalType Type,
    DateTimeOffset OccurredAtUtc,
    CallOutcome? Outcome = null,
    string? EndReason = null);

public sealed record CallTransition(bool Accepted, bool StateChanged, CallSessionState State, string? ErrorCode);

public static class RegistrationReducer
{
    public static RegistrationState Apply(RegistrationState current, SipSignalType signal) => signal switch
    {
        SipSignalType.RegistrationRegistering when current is RegistrationState.Unregistered or RegistrationState.Failed
            => RegistrationState.Registering,
        SipSignalType.RegistrationRegistered => RegistrationState.Registered,
        SipSignalType.RegistrationUnregistered => RegistrationState.Unregistered,
        SipSignalType.RegistrationFailed => RegistrationState.Failed,
        _ => current
    };
}

public static class CallReducer
{
    public static CallSessionState CreateOutbound(
        string callId,
        string commandId,
        DateTimeOffset occurredAtUtc,
        string maskedDestination) =>
        new(callId, commandId, CallDirection.Outbound, CallState.Created, occurredAtUtc, null, null, null, null, maskedDestination);

    public static CallSessionState CreateInbound(
        string callId,
        DateTimeOffset occurredAtUtc,
        string maskedCaller) =>
        new(callId, null, CallDirection.Inbound, CallState.Created, occurredAtUtc, null, null, null, null, maskedCaller);

    public static CallTransition Apply(CallSessionState current, CallSignal signal)
    {
        if (current.State == CallState.Ended)
        {
            return new CallTransition(false, false, current, "call_already_ended");
        }

        CallState? next = signal.Type switch
        {
            CallSignalType.Dial when current.Direction == CallDirection.Outbound && current.State == CallState.Created
                => CallState.Dialing,
            CallSignalType.Trying when current.Direction == CallDirection.Outbound && current.State is CallState.Dialing
                => CallState.Trying,
            CallSignalType.Ringing when current.Direction == CallDirection.Outbound && current.State is CallState.Dialing or CallState.Trying
                => CallState.Ringing,
            CallSignalType.Incoming when current.Direction == CallDirection.Inbound && current.State == CallState.Created
                => CallState.Incoming,
            CallSignalType.Answering when current.Direction == CallDirection.Inbound && current.State == CallState.Incoming
                => CallState.Answering,
            CallSignalType.Connected when current.State is CallState.Dialing or CallState.Trying or CallState.Ringing or CallState.Incoming or CallState.Answering
                => CallState.Connected,
            CallSignalType.Ending when current.State != CallState.Ended
                => CallState.Ending,
            CallSignalType.End when signal.Outcome.HasValue
                => CallState.Ended,
            _ => null
        };

        if (!next.HasValue)
        {
            if (signal.Type == CallSignalType.Connected && current.State == CallState.Connected)
            {
                return new CallTransition(true, false, current, null);
            }

            return new CallTransition(false, false, current, "call_invalid_state");
        }

        CallSessionState updated = current with
        {
            State = next.Value,
            AnsweredAtUtc = next == CallState.Connected ? current.AnsweredAtUtc ?? signal.OccurredAtUtc : current.AnsweredAtUtc,
            EndedAtUtc = next == CallState.Ended ? signal.OccurredAtUtc : current.EndedAtUtc,
            Outcome = next == CallState.Ended ? signal.Outcome : current.Outcome,
            EndReason = next == CallState.Ended ? signal.EndReason : current.EndReason
        };

        return new CallTransition(true, updated != current, updated, null);
    }
}

public static class SensitiveValueMasker
{
    public static string MaskRemoteParty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        string trimmed = value.Trim();
        if (trimmed.Length <= 4)
        {
            return new string('x', trimmed.Length);
        }

        int suffixLength = Math.Min(4, Math.Max(1, trimmed.Length - 2));
        int maskedLength = trimmed.Length - suffixLength;
        return $"{new string('x', maskedLength)}{trimmed[^suffixLength..]}";
    }
}
