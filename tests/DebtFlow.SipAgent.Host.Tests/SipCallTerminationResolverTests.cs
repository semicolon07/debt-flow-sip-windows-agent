using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Host;

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
    public void Resolve_WhenDialogueIsMissing_DoesNotInventRemoteHangup()
    {
        var active = new SipCallHandle("runtime-active", 2, "public-active");

        SipCallHandle? resolved = SipCallTerminationResolver.Resolve(
            null,
            new Dictionary<string, SipCallHandle>(),
            active);

        Assert.Null(resolved);
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
}
