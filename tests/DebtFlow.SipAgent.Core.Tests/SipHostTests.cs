using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Core.Tests;

public sealed class SipHostTests
{
    [Theory]
    [InlineData("192.0.2.10", "192.0.2.10")]
    [InlineData("PBX.Branch.Example.", "pbx.branch.example")]
    [InlineData("pbx-01", "pbx-01")]
    public void TryCanonicalize_AcceptsSupportedIpv4AndAsciiHostname(string value, string expected)
    {
        Assert.True(SipHost.TryCanonicalize(value, out string canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("2001:db8::10")]
    [InlineData("[2001:db8::10]")]
    [InlineData("fe80::1%12")]
    [InlineData("192.0.2.10:5060")]
    [InlineData("sip.example.test:5060")]
    [InlineData("sip://pbx.example.test")]
    [InlineData("_sip._udp.example.test")]
    [InlineData("pbx_01.example.test")]
    [InlineData("สาขา.example.test")]
    [InlineData("999.999.999.999")]
    public void TryCanonicalize_RejectsUnsupportedHostForms(string value)
    {
        Assert.False(SipHost.TryCanonicalize(value, out _));
    }
}
