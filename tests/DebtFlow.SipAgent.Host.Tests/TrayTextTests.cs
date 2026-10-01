using System.Globalization;
using DebtFlow.SipAgent.Host;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class TrayTextTests
{
    [Fact]
    public void State_EveryRuntimeWireStateHasEnglishAndThaiText()
    {
        string[] states =
        [
            "starting", "ready", "degraded", "stopping",
            "unconfigured", "unregistered", "registering", "registered", "unregistering", "retrying", "failed",
            "dialing", "trying", "ringing", "incoming", "answering", "connected", "ending", "ended"
        ];
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (string culture in new[] { "en-US", "th-TH" })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                foreach (string state in states)
                {
                    Assert.NotEqual(TrayText.Get("StateUnknown"), TrayText.State(state));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void State_UsesCurrentWindowsUiCultureResources()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("th-TH");
            Assert.Equal("ลงทะเบียนแล้ว", TrayText.State("registered"));

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Registered", TrayText.State("registered"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
