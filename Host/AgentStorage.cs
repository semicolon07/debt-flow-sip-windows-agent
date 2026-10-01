using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;

namespace DebtFlow.SipAgent.Host;

public static class AgentStoragePaths
{
    public static string DatabasePath => Path.Combine(ResolveLocalApplicationData(), "agent-v1.db");
    public static string LogDirectory => Path.Combine(ResolveLocalApplicationData(), "Logs");

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
    public static async Task<(IAgentEventStore Store, bool Available)> CreateAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var store = new SqliteAgentEventStore(databasePath);
        try
        {
            await store.InitializeAsync(cancellationToken);
            return (store, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await store.DisposeAsync();
            return (new UnavailableAgentEventStore(), false);
        }
    }
}
