namespace DebtFlow.SipAgent.Protocol;

public static class OriginPolicy
{
    public static bool IsAllowed(string? origin, IReadOnlySet<string> allowedOrigins) =>
        !string.IsNullOrWhiteSpace(origin) && allowedOrigins.Contains(origin);
}
