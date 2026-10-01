using System.Globalization;
using System.Resources;

namespace DebtFlow.SipAgent.Host;

public static class TrayText
{
    private static readonly ResourceManager Resources = new(
        "DebtFlow.SipAgent.Host.Resources.TrayStrings",
        typeof(TrayText).Assembly);

    public static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? Resources.GetString(name, CultureInfo.InvariantCulture) ?? name;

    public static string Format(string name, params object[] values) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), values);

    public static string State(string state) => state switch
    {
        "starting" => Get("StateStarting"),
        "ready" => Get("StateReady"),
        "degraded" => Get("StateDegraded"),
        "stopping" => Get("StateStopping"),
        "unconfigured" => Get("StateUnconfigured"),
        "unregistered" => Get("StateUnregistered"),
        "registering" => Get("StateRegistering"),
        "registered" => Get("StateRegistered"),
        "unregistering" => Get("StateUnregistering"),
        "retrying" => Get("StateRetrying"),
        "failed" => Get("StateFailed"),
        "calling" => Get("StateCalling"),
        "dialing" => Get("StateDialing"),
        "trying" => Get("StateTrying"),
        "ringing" => Get("StateRinging"),
        "incoming" => Get("StateIncoming"),
        "answering" => Get("StateAnswering"),
        "connected" => Get("StateConnected"),
        "ending" => Get("StateEnding"),
        "ended" => Get("StateEnded"),
        "unknown" => Get("StateUnknown"),
        _ => Get("StateUnknown")
    };
}
