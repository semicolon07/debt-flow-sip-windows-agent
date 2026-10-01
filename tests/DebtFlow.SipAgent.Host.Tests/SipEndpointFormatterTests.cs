using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class SipEndpointFormatterTests
{
    [Fact]
    public void FormatRegistrar_UsesCanonicalIpv4AndBracketedIpv6()
    {
        (string Host, int Port, string Expected)[] cases =
        [
            ("192.0.2.10", 5060, "192.0.2.10"),
            ("192.0.2.10", 5070, "192.0.2.10:5070"),
            ("2001:db8::10", 5060, "[2001:db8::10]"),
            ("2001:db8::10", 5070, "[2001:db8::10]:5070")
        ];

        foreach ((string host, int port, string expected) in cases)
        {
            Assert.Equal(
                expected,
                SipEndpointFormatter.FormatRegistrar(new SipConfiguration(host, port, "user", "password")));
        }
    }
}
