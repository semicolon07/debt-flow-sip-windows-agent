using Microsoft.Extensions.Logging;

namespace DebtFlow.SipAgent.Host;

public sealed class RedactingSipLoggerProvider(ILoggerFactory loggerFactory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RedactingSipLogger(
        loggerFactory.CreateLogger("SIPSorcery"),
        categoryName);

    public void Dispose()
    {
    }

    private sealed class RedactingSipLogger(ILogger target, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => target.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            target.Log(
                logLevel,
                eventId,
                new SipLogState(categoryName, eventId.Id, exception?.GetType().Name),
                null,
                static (safe, _) => $"SIP library event {safe.Category} {safe.EventId} {safe.ErrorType}");
        }
    }

    private sealed record SipLogState(string Category, int EventId, string? ErrorType);
}
