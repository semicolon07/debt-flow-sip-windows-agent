using System.Net;
using System.Net.Sockets;
using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class SipHostResolverTests
{
    [Fact]
    public async Task ResolveIPv4Async_ResolvesHostnameOnEachRegistrationAttempt()
    {
        int attempts = 0;
        Task<IPAddress[]> Resolve(string host, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("pbx.branch.example", host);
            attempts++;
            return Task.FromResult(new[] { IPAddress.Parse($"192.0.2.{attempts}") });
        }

        IPAddress first = await SipHostResolver.ResolveIPv4Async(
            "PBX.Branch.Example.", Resolve, CancellationToken.None);
        IPAddress second = await SipHostResolver.ResolveIPv4Async(
            "pbx.branch.example", Resolve, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("192.0.2.1"), first);
        Assert.Equal(IPAddress.Parse("192.0.2.2"), second);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ResolveIPv4Async_RejectsAaaaOnlyResult()
    {
        static Task<IPAddress[]> Resolve(string _, CancellationToken __) =>
            Task.FromResult(new[] { IPAddress.Parse("2001:db8::10") });

        SocketException exception = await Assert.ThrowsAsync<SocketException>(() =>
            SipHostResolver.ResolveIPv4Async("pbx.example", Resolve, CancellationToken.None));

        Assert.Equal(SocketError.HostNotFound, exception.SocketErrorCode);
    }

    [Fact]
    public async Task ResolveIPv4Async_DoesNotQueryDnsForIpv4Literal()
    {
        bool called = false;
        Task<IPAddress[]> Resolve(string _, CancellationToken __)
        {
            called = true;
            return Task.FromResult(Array.Empty<IPAddress>());
        }

        IPAddress address = await SipHostResolver.ResolveIPv4Async(
            "192.0.2.10", Resolve, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("192.0.2.10"), address);
        Assert.False(called);
    }
}
