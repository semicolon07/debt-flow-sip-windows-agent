using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;

namespace DebtFlow.SipAgent.Host;

public static class AgentStoragePaths
{
    public static string RootDirectory => ResolveLocalApplicationData();
    public static string ConfigurationPath => Path.Combine(RootDirectory, "agentsettings.json");
    public static string DatabasePath => Path.Combine(RootDirectory, "agent-v1.db");
    public static string LogDirectory => Path.Combine(RootDirectory, "Logs");
    public static string DiagnosticDirectory => Path.Combine(RootDirectory, "Diagnostics");

    private static string ResolveLocalApplicationData()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("local_application_data_unavailable");
        }

        return Path.Combine(root, "DebtFlow", "SipAgent");
    }
}

public static class AgentEventStoreFactory
{
    public static async Task<(IAgentEventStore Store, bool Available, string? FailureCode)> CreateAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var store = new SqliteAgentEventStore(databasePath);
        try
        {
            await store.InitializeAsync(cancellationToken);
            return (store, true, null);
        }
        catch (AgentStoreException exception)
        {
            await store.DisposeAsync();
            return (new UnavailableAgentEventStore(exception.ErrorCode), false, exception.ErrorCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await store.DisposeAsync();
            return (new UnavailableAgentEventStore("outbox_unavailable"), false, "outbox_unavailable");
        }
    }
}
