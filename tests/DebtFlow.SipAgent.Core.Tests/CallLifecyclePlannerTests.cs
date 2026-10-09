using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Core.Tests;

public sealed class CallLifecyclePlannerTests
{
    [Theory]
    [InlineData(CallDirection.Outbound, CallSignalType.Dial, CallState.Dialing, "outbound")]
    [InlineData(CallDirection.Inbound, CallSignalType.Incoming, CallState.Incoming, "inbound")]
    public void Start_ProducesCreatedAndInitialStateAsOneNonTerminalPlan(
        CallDirection direction,
        CallSignalType signal,
        CallState expectedState,
        string expectedDirection)
    {
        DateTimeOffset started = new(2026, 10, 9, 1, 0, 0, TimeSpan.Zero);
        CallSessionState created = direction == CallDirection.Outbound
            ? CallReducer.CreateOutbound(NewId(), NewId(), started, "***5678")
            : CallReducer.CreateInbound(NewId(), started, "***5678");

        CallLifecyclePlan plan = CallLifecyclePlanner.Start(
            created,
            new CallSignal(signal, started.AddSeconds(1)));

        Assert.Equal(expectedState, plan.State.State);
        Assert.False(plan.Terminal);
        Assert.Equal(["call.created", "call.state_changed"], plan.Events.Select(item => item.EventType));
        Assert.Contains(expectedDirection, System.Text.Json.JsonSerializer.Serialize(plan.Events[0].Data));
    }

    [Fact]
    public void End_ProducesStateChangedAndEndedWithDeterministicDurations()
    {
        DateTimeOffset started = new(2026, 10, 9, 1, 0, 0, TimeSpan.Zero);
        CallSessionState connected = CallReducer.CreateOutbound(NewId(), NewId(), started, "***5678") with
        {
            State = CallState.Connected,
            AnsweredAtUtc = started.AddSeconds(5)
        };

        CallLifecyclePlan plan = Assert.IsType<CallLifecyclePlan>(
            CallLifecyclePlanner.End(
                connected,
                started.AddSeconds(20),
                CallOutcome.Completed,
                "remote_hangup"));

        Assert.True(plan.Terminal);
        Assert.Equal(CallState.Ended, plan.State.State);
        Assert.Equal(["call.state_changed", "call.ended"], plan.Events.Select(item => item.EventType));
        string endedJson = System.Text.Json.JsonSerializer.Serialize(plan.Events[1].Data);
        Assert.Contains("15000", endedJson, StringComparison.Ordinal);
        Assert.Contains("20000", endedJson, StringComparison.Ordinal);
    }

    private static string NewId() => Guid.NewGuid().ToString("D").ToLowerInvariant();
}
