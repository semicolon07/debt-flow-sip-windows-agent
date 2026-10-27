using System.Net;
using System.Net.Sockets;

namespace DebtFlow.SipAgent.Application;

public static class SipHost
{
    public static bool TryCanonicalize(string? value, out string canonical)
    {
        canonical = string.Empty;
        string host = value?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            canonical = address.ToString();
            return true;
        }

        host = host.EndsWith(".", StringComparison.Ordinal) ? host[..^1] : host;
        if (host.Length is 0 or > 253 || host.All(character => char.IsAsciiDigit(character) || character == '.'))
        {
            return false;
        }

        string[] labels = host.Split('.');
        foreach (string label in labels)
        {
            if (label.Length is 0 or > 63 || !IsAsciiLetterOrDigit(label[0]) ||
                !IsAsciiLetterOrDigit(label[^1]) ||
                label.Any(character => !IsAsciiLetterOrDigit(character) && character != '-'))
            {
                return false;
            }
        }

        canonical = host.ToLowerInvariant();
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
