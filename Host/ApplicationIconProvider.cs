using System.Drawing;
using System.Runtime.InteropServices;

namespace DebtFlow.SipAgent.Host;

internal static class ApplicationIconProvider
{
    public static Icon Load() => Load(Environment.ProcessPath);

    internal static Icon Load(string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            try
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(executablePath);
                if (extracted != null)
                {
                    return extracted;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or
                                               ExternalException or
                                               IOException or
                                               UnauthorizedAccessException)
            {
                // The tray must remain available even if Windows cannot read the executable icon resource.
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
