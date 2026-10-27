using System.Net;
using System.Net.Sockets;
using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Host;

internal static class SipHostResolver
{
    public static Task<IPAddress> ResolveIPv4Async(string host, CancellationToken cancellationToken) =>
        ResolveIPv4Async(
            host,
            static (name, token) => Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, token),
            cancellationToken);

    internal static async Task<IPAddress> ResolveIPv4Async(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        CancellationToken cancellationToken)
    {
        if (!SipHost.TryCanonicalize(host, out string canonical))
        {
            throw new ArgumentException("SIP host is invalid.", nameof(host));
        }

        if (IPAddress.TryParse(canonical, out IPAddress? literal))
        {
            return literal;
        }

        IPAddress[] addresses = await resolve(canonical, cancellationToken).ConfigureAwait(false);
        IPAddress? selected = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        return selected ?? throw new SocketException((int)SocketError.HostNotFound);
    }
}
