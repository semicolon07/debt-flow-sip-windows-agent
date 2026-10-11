using DebtFlow.SipAgent.Application;
using SIPSorcery.SIP;

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

    internal static SipCallHandle? ResolveMapped(
        string? sipCallId,
        IReadOnlyDictionary<string, SipCallHandle> mappedCalls,
        SipCallHandle? activeCall)
    {
        if (activeCall == null || string.IsNullOrWhiteSpace(sipCallId))
        {
            return null;
        }

        return mappedCalls.TryGetValue(sipCallId, out SipCallHandle? mappedCall) &&
               IsSameCall(mappedCall, activeCall)
            ? mappedCall
            : null;
    }

    internal static bool IsTerminalInviteFailure(SIPMethodsEnum method, int statusCode) =>
        method == SIPMethodsEnum.INVITE &&
        statusCode is >= 400 and <= 699 &&
        statusCode is not 401 and not 407;

    private static bool IsSameCall(SipCallHandle left, SipCallHandle right) =>
        left.Generation == right.Generation &&
        string.Equals(left.RuntimeCallId, right.RuntimeCallId, StringComparison.Ordinal);
}
