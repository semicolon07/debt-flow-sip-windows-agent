using DebtFlow.SipAgent.Application;

namespace DebtFlow.SipAgent.Host;

public static class SipEndpointFormatter
{
    public static string FormatRegistrar(SipConfiguration configuration, bool useTcp = false)
    {
        if (!SipHost.TryCanonicalize(configuration.Host, out string host))
        {
            throw new ArgumentException("SIP host must be canonical IPv4 or an ASCII hostname.", nameof(configuration));
        }

        string endpoint = configuration.Port == 5060 ? host : $"{host}:{configuration.Port}";
        return useTcp ? $"{endpoint};transport=tcp" : endpoint;
    }

    public static string FormatDestination(SipConfiguration configuration, string destination, bool useTcp = false) =>
        $"sip:{destination}@{FormatRegistrar(configuration, useTcp)}";
}
