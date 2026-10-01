using Microsoft.Extensions.Logging;
using SIPSorcery;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Host;
using DebtFlow.SipAgent.Persistence;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        options.UseUtcTimestamp = true;
    });
});

using ILoggerFactory sipLoggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Warning);
    builder.AddProvider(new ForwardingLoggerProvider(loggerFactory));
});
SIPSorcery.LogFactory.Set(sipLoggerFactory);

ILogger logger = loggerFactory.CreateLogger("DebtFlow.SipAgent");
string databasePath = ResolveDatabasePath();
IReadOnlySet<string> allowedOrigins = ResolveAllowedOrigins(args);

logger.LogInformation("Starting Debt Flow SIP Agent protocol V1");
logger.LogInformation("Allowed WebSocket origins: {OriginCount}", allowedOrigins.Count);

IAgentEventStore eventStore = await CreateEventStoreAsync(databasePath, logger, shutdown.Token);
await using IAgentEventStore eventStoreLifetime = eventStore;
bool durableStoreAvailable = eventStore is SqliteAgentEventStore;
var publisher = new WebSocketEventPublisher();
await using var sipRuntime = new SipRuntime(loggerFactory.CreateLogger<SipRuntime>());
await using var coordinator = new AgentCoordinator(
    sipRuntime,
    eventStore,
    publisher,
    new SystemAgentClock(),
    new GuidAgentIdGenerator(),
    durableStoreAvailable);
var dispatcher = new V1CommandDispatcher(coordinator, eventStore, new SystemAgentClock());
await using var server = new LocalWebSocketServer(
    coordinator,
    eventStore,
    publisher,
    dispatcher,
    loggerFactory.CreateLogger<LocalWebSocketServer>(),
    allowedOrigins);

try
{
    await server.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

logger.LogInformation("Debt Flow SIP Agent stopped");

static string ResolveDatabasePath()
{
    string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(root))
    {
        throw new InvalidOperationException("Local application data directory is unavailable.");
    }

    return Path.Combine(root, "DebtFlow", "SipAgent", "agent-v1.db");
}

static IReadOnlySet<string> ResolveAllowedOrigins(string[] arguments)
{
    var configured = new HashSet<string>(StringComparer.Ordinal)
    {
        "http://localhost:8765",
        "http://127.0.0.1:8765"
    };

    for (int index = 0; index < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], "--allowed-origin", StringComparison.Ordinal) || index + 1 >= arguments.Length)
        {
            continue;
        }

        if (Uri.TryCreate(arguments[++index], UriKind.Absolute, out Uri? origin) &&
            (origin.Scheme == Uri.UriSchemeHttp || origin.Scheme == Uri.UriSchemeHttps))
        {
            configured.Add(origin.GetLeftPart(UriPartial.Authority));
        }
    }

    return configured;
}

static async Task<IAgentEventStore> CreateEventStoreAsync(
    string databasePath,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var store = new SqliteAgentEventStore(databasePath);
    try
    {
        await store.InitializeAsync(cancellationToken);
        return store;
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        await store.DisposeAsync();
        logger.LogError(
            "Durable event store is unavailable ({ErrorType}); agent starts degraded and blocks SIP registration/calls",
            exception.GetType().Name);
        return new UnavailableAgentEventStore();
    }
}

sealed class ForwardingLoggerProvider(ILoggerFactory loggerFactory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => loggerFactory.CreateLogger(categoryName);
    public void Dispose()
    {
    }
}
