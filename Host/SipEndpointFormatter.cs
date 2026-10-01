using System.Net;
using System.Net.Sockets;
using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Host;

public static class SipEndpointFormatter
{
    public static string FormatRegistrar(SipConfiguration configuration)
    {
        if (!IPAddress.TryParse(configuration.Host, out IPAddress? address))
        {
            throw new ArgumentException("SIP host must be an IP literal.", nameof(configuration));
        }

        string host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return configuration.Port == 5060 ? host : $"{host}:{configuration.Port}";
    }
}
