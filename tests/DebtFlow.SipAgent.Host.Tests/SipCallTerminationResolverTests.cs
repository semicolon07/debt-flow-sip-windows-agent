using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Host;
using SIPSorcery.SIP;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class SipCallTerminationResolverTests
{
    [Fact]
    public void Resolve_WhenDialogueCallIdIsUnmapped_ReturnsActiveCall()
    {
        var active = new SipCallHandle("runtime-active", 2, "public-active");
        var stale = new SipCallHandle("runtime-stale", 1, "public-stale");
        IReadOnlyDictionary<string, SipCallHandle> mapped =
            new Dictionary<string, SipCallHandle>(StringComparer.OrdinalIgnoreCase)
            {
                ["stale-call-id"] = stale
            };

        SipCallHandle? resolved = SipCallTerminationResolver.Resolve(
            "unmapped-call-id",
            mapped,
            active);

        Assert.Same(active, resolved);
    }

    [Fact]
    public void Resolve_WhenDialogueIsMissing_ReturnsActiveCall()
    {
        var active = new SipCallHandle("runtime-active", 2, "public-active");

        SipCallHandle? resolved = SipCallTerminationResolver.Resolve(
            null,
            new Dictionary<string, SipCallHandle>(),
            active);

        Assert.Same(active, resolved);
    }

    [Fact]
    public void Resolve_WhenThereIsNoActiveCall_IgnoresStaleMapping()
    {
        var stale = new SipCallHandle("runtime-stale", 1, "public-stale");
        IReadOnlyDictionary<string, SipCallHandle> mapped =
            new Dictionary<string, SipCallHandle>
            {
                ["stale-call-id"] = stale
            };

        SipCallHandle? resolved = SipCallTerminationResolver.Resolve(
            "stale-call-id",
            mapped,
            null);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveMapped_WhenCallIdIsUnknown_DoesNotFallBackToActiveCall()
    {
        var active = new SipCallHandle("runtime-active", 2, "public-active");

        SipCallHandle? resolved = SipCallTerminationResolver.ResolveMapped(
            "unknown-call-id",
            new Dictionary<string, SipCallHandle>(),
            active);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveMapped_WhenCallIdBelongsToActiveCall_ReturnsMappedCall()
    {
        var active = new SipCallHandle("runtime-active", 2, "public-active");
        var mappedCalls = new Dictionary<string, SipCallHandle>
        {
            ["active-call-id"] = active
        };

        SipCallHandle? resolved = SipCallTerminationResolver.ResolveMapped(
            "active-call-id",
            mappedCalls,
            active);

        Assert.Same(active, resolved);
    }

    [Fact]
    public void ResolveMapped_WhenCallIdBelongsToOlderGeneration_DoesNotResolveCurrentCall()
    {
        var current = new SipCallHandle("runtime-current", 3, "public-current");
        var previous = new SipCallHandle("runtime-previous", 2, "public-previous");
        var mappedCalls = new Dictionary<string, SipCallHandle>
        {
            ["previous-call-id"] = previous
        };

        SipCallHandle? resolved = SipCallTerminationResolver.ResolveMapped(
            "previous-call-id",
            mappedCalls,
            current);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData(SIPMethodsEnum.INVITE, 486, true)]
    [InlineData(SIPMethodsEnum.INVITE, 603, true)]
    [InlineData(SIPMethodsEnum.INVITE, 401, false)]
    [InlineData(SIPMethodsEnum.INVITE, 407, false)]
    [InlineData(SIPMethodsEnum.INVITE, 302, false)]
    [InlineData(SIPMethodsEnum.INVITE, 400, true)]
    [InlineData(SIPMethodsEnum.INVITE, 200, false)]
    [InlineData(SIPMethodsEnum.INVITE, 700, false)]
    [InlineData(SIPMethodsEnum.BYE, 486, false)]
    public void IsTerminalInviteFailure_OnlyAcceptsNonAuthenticationFinalInviteFailures(
        SIPMethodsEnum method,
        int statusCode,
        bool expected)
    {
        Assert.Equal(expected, SipCallTerminationResolver.IsTerminalInviteFailure(method, statusCode));
    }
}
