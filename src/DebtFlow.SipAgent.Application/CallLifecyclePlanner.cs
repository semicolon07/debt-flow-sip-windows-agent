namespace DebtFlow.SipAgent.Application;

public sealed record PlannedCallEvent(string EventType, CallSessionState State, object Data);

public sealed record CallLifecyclePlan(
    CallSessionState State,
    IReadOnlyList<PlannedCallEvent> Events,
    bool Terminal);

/// <summary>
/// Pure call-state policy. It never mutates coordinator state or performs I/O.
/// </summary>
public static class CallLifecyclePlanner
{
    public static CallLifecyclePlan Start(
        CallSessionState created,
        CallSignal initialSignal)
    {
        CallTransition transition = CallReducer.Apply(created, initialSignal);
        if (!transition.Accepted || !transition.StateChanged)
        {
            throw new InvalidOperationException(transition.ErrorCode ?? "call_invalid_state");
        }

        string direction = AgentCoordinator.ToWire(created.Direction);
        return new CallLifecyclePlan(
            transition.State,
            [
                new PlannedCallEvent(
                    "call.created",
                    created,
                    new { direction, remoteParty = created.RemoteParty }),
                new PlannedCallEvent(
                    "call.state_changed",
                    transition.State,
                    new { direction })
            ],
            Terminal: false);
    }

    public static CallLifecyclePlan? Transition(
        CallSessionState current,
        CallSignal signal,
        object data)
    {
        CallTransition transition = CallReducer.Apply(current, signal);
        return !transition.Accepted || !transition.StateChanged
            ? null
            : new CallLifecyclePlan(
                transition.State,
                [new PlannedCallEvent("call.state_changed", transition.State, data)],
                Terminal: false);
    }

    public static CallLifecyclePlan? End(
        CallSessionState current,
        DateTimeOffset occurredAtUtc,
        CallOutcome outcome,
        string reason)
    {
        CallTransition transition = CallReducer.Apply(
            current,
            new CallSignal(CallSignalType.End, occurredAtUtc, outcome, reason));
        if (!transition.Accepted || !transition.StateChanged)
        {
            return null;
        }

        CallSessionState ended = transition.State;
        long? talkDurationMs = ended.AnsweredAtUtc.HasValue
            ? Math.Max(0, (long)(ended.EndedAtUtc!.Value - ended.AnsweredAtUtc.Value).TotalMilliseconds)
            : null;
        long totalDurationMs = Math.Max(
            0,
            (long)(ended.EndedAtUtc!.Value - ended.StartedAtUtc).TotalMilliseconds);
        return new CallLifecyclePlan(
            ended,
            [
                new PlannedCallEvent(
                    "call.state_changed",
                    ended,
                    new { outcome = AgentCoordinator.ToWire(outcome), endReason = reason }),
                new PlannedCallEvent(
                    "call.ended",
                    ended,
                    new
                    {
                        direction = AgentCoordinator.ToWire(ended.Direction),
                        outcome = AgentCoordinator.ToWire(outcome),
                        endReason = reason,
                        talkDurationMs,
                        totalDurationMs
                    })
            ],
            Terminal: true);
    }
}
