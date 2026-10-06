using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Core.Tests;

public sealed class StateReducerTests
{
    [Fact]
    public void RegistrationUnregisteredSignal_DoesNotReintroduceConfigurationAfterExplicitStop()
    {
        RegistrationState state = RegistrationReducer.Apply(
            RegistrationState.Unconfigured,
            SipSignalType.RegistrationUnregistered);

        Assert.Equal(RegistrationState.Unconfigured, state);
    }

    [Fact]
    public void LateRegistrationSuccessOrFailure_DoesNotChangeUnconfiguredState()
    {
        Assert.Equal(
            RegistrationState.Unconfigured,
            RegistrationReducer.Apply(RegistrationState.Unconfigured, SipSignalType.RegistrationRegistered));
        Assert.Equal(
            RegistrationState.Unconfigured,
            RegistrationReducer.Apply(RegistrationState.Unconfigured, SipSignalType.RegistrationFailed));
    }

    private static readonly DateTimeOffset StartedAt = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OutboundCall_ValidLifecycle_EndsOnceWithOutcome()
    {
        CallSessionState state = CallReducer.CreateOutbound("call", "command", StartedAt, "0812345678");

        state = Apply(state, CallSignalType.Dial, 1);
        state = Apply(state, CallSignalType.Trying, 2);
        state = Apply(state, CallSignalType.Ringing, 3);
        state = Apply(state, CallSignalType.Connected, 4);
        CallTransition ended = CallReducer.Apply(
            state,
            new CallSignal(CallSignalType.End, StartedAt.AddSeconds(10), CallOutcome.Completed, "remote_hangup"));

        Assert.True(ended.Accepted);
        Assert.Equal(CallState.Ended, ended.State.State);
        Assert.Equal(CallOutcome.Completed, ended.State.Outcome);
        Assert.Equal("remote_hangup", ended.State.EndReason);

        CallTransition duplicate = CallReducer.Apply(
            ended.State,
            new CallSignal(CallSignalType.End, StartedAt.AddSeconds(11), CallOutcome.Failed, "late_failure"));
        Assert.False(duplicate.Accepted);
        Assert.Equal(CallOutcome.Completed, duplicate.State.Outcome);
    }

    [Fact]
    public void DuplicateConnected_DoesNotChangeStateAgain()
    {
        CallSessionState state = CallReducer.CreateOutbound("call", "command", StartedAt, "0812345678");
        state = Apply(state, CallSignalType.Dial, 1);
        state = Apply(state, CallSignalType.Connected, 2);

        CallTransition duplicate = CallReducer.Apply(
            state,
            new CallSignal(CallSignalType.Connected, StartedAt.AddSeconds(3)));

        Assert.True(duplicate.Accepted);
        Assert.False(duplicate.StateChanged);
        Assert.Equal(StartedAt.AddSeconds(2), duplicate.State.AnsweredAtUtc);
    }

    [Theory]
    [InlineData("081-234-5678", "0812345678")]
    [InlineData("＋66812345678", "+66812345678")]
    [InlineData("sip:1001@example.test", "unknown")]
    [InlineData(null, "unknown")]
    public void NormalizeRemoteParty_ReturnsCanonicalDialableValue(string? input, string expected)
    {
        Assert.Equal(expected, RemotePartyNormalizer.Normalize(input));
    }

    private static CallSessionState Apply(CallSessionState state, CallSignalType signal, int seconds)
    {
        CallTransition transition = CallReducer.Apply(state, new CallSignal(signal, StartedAt.AddSeconds(seconds)));
        Assert.True(transition.Accepted);
        return transition.State;
    }
}
