using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Host;

internal static class SipCallTerminationResolver
{
    internal static SipCallHandle? Resolve(
        string? sipCallId,
        IReadOnlyDictionary<string, SipCallHandle> mappedCalls,
        SipCallHandle? activeCall)
    {
        if (activeCall == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(sipCallId) &&
            mappedCalls.TryGetValue(sipCallId, out SipCallHandle? mappedCall) &&
            IsSameCall(mappedCall, activeCall))
        {
            return mappedCall;
        }

        return activeCall;
    }

    private static bool IsSameCall(SipCallHandle left, SipCallHandle right) =>
        left.Generation == right.Generation &&
        string.Equals(left.RuntimeCallId, right.RuntimeCallId, StringComparison.Ordinal);
}
