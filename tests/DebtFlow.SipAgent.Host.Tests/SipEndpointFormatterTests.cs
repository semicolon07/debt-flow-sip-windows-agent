using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class SipEndpointFormatterTests
{
    [Fact]
    public void FormatRegistrar_UsesCanonicalIpv4AndHostname()
    {
        (string Host, int Port, string Expected)[] cases =
        [
            ("192.0.2.10", 5060, "192.0.2.10"),
            ("192.0.2.10", 5070, "192.0.2.10:5070"),
            ("PBX.Branch.Example.", 5060, "pbx.branch.example"),
            ("pbx.branch.example", 5070, "pbx.branch.example:5070")
        ];

        foreach ((string host, int port, string expected) in cases)
        {
            Assert.Equal(
                expected,
                SipEndpointFormatter.FormatRegistrar(new SipConfiguration(host, port, "user", "password")));
        }
    }

    [Fact]
    public void FormatRegistrar_RejectsIpv6AndEmbeddedPort()
    {
        foreach (string host in new[] { "2001:db8::10", "[2001:db8::10]", "pbx.example:5060" })
        {
            Assert.Throws<ArgumentException>(() =>
                SipEndpointFormatter.FormatRegistrar(new SipConfiguration(host, 5060, "user", "password")));
        }
    }

    [Fact]
    public void FormatRegistrar_AppendsTcpTransportWithoutAddingDefaultPort()
    {
        var configuration = new SipConfiguration("192.0.2.10", 5060, "user", "password");

        Assert.Equal("192.0.2.10;transport=tcp", SipEndpointFormatter.FormatRegistrar(configuration, true));
        Assert.Equal("sip:1001@192.0.2.10;transport=tcp", SipEndpointFormatter.FormatDestination(configuration, "1001", true));
    }
}
