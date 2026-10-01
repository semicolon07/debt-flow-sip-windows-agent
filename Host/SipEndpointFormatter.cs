using System.Net;
using System.Net.Sockets;
using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Host;

public static class SipEndpointFormatter
{
    public static string FormatRegistrar(SipConfiguration configuration, bool useTcp = false)
    {
        if (!IPAddress.TryParse(configuration.Host, out IPAddress? address))
        {
            throw new ArgumentException("SIP host must be an IP literal.", nameof(configuration));
        }

        string host = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        string endpoint = configuration.Port == 5060 ? host : $"{host}:{configuration.Port}";
        return useTcp ? $"{endpoint};transport=tcp" : endpoint;
    }

    public static string FormatDestination(SipConfiguration configuration, string destination, bool useTcp = false) =>
        $"sip:{destination}@{FormatRegistrar(configuration, useTcp)}";
}
