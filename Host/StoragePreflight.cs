using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DebtFlow.SipAgent.Host;

public sealed record StoragePreflightResult(
    int SchemaVersion,
    long PendingEvents,
    bool ActiveCallPresent,
    bool SafeToUpgrade,
    string Code);

public static class StoragePreflight
{
    public const int MigrationRequiredExitCode = 20;
    public const int StorageUnavailableExitCode = 21;

    public static async Task<StoragePreflightResult> InspectAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return new(0, 0, false, true, "storage_not_created");
        }

        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            string integrity = Convert.ToString(
                await ScalarAsync(connection, "PRAGMA quick_check;", cancellationToken),
                CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new(0, 0, false, false, "outbox_corrupt");
            }

            int schemaVersion = Convert.ToInt32(
                await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken),
                CultureInfo.InvariantCulture);
            if (schemaVersion > AgentReleaseMetadata.StorageSchemaVersion)
            {
                return new(schemaVersion, 0, false, false, "outbox_schema_newer");
            }

            if (schemaVersion == 0)
            {
                long userTables = Convert.ToInt64(
                    await ScalarAsync(
                        connection,
                        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';",
                        cancellationToken),
                    CultureInfo.InvariantCulture);
                return userTables == 0
                    ? new(0, 0, false, true, "storage_empty")
                    : new(0, 0, false, false, "outbox_schema_invalid");
            }

            bool hasMetadata = await TableExistsAsync(connection, "AgentMetadata", cancellationToken);
            bool hasDurableEvents = await TableExistsAsync(connection, "DurableEvents", cancellationToken);
            bool hasProcessedCommands = await TableExistsAsync(connection, "ProcessedCommands", cancellationToken);
            bool hasActiveCallJournal = await TableExistsAsync(connection, "ActiveCallJournal", cancellationToken);
            if (!hasMetadata || !hasDurableEvents || !hasProcessedCommands ||
                schemaVersion >= 2 && !hasActiveCallJournal)
            {
                return new(schemaVersion, 0, false, false, "outbox_schema_invalid");
            }
            if (!await HasExpectedSchemaAsync(connection, schemaVersion, cancellationToken))
            {
                return new(schemaVersion, 0, false, false, "outbox_schema_invalid");
            }

            long acknowledged = 0;
            if (hasMetadata)
            {
                object? value = await ScalarAsync(
                    connection,
                    "SELECT MetadataValue FROM AgentMetadata WHERE MetadataKey = 'last_acknowledged_sequence';",
                    cancellationToken);
                _ = long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out acknowledged);
            }

            long pending = hasDurableEvents
                ? Convert.ToInt64(
                    await ScalarAsync(
                        connection,
                        "SELECT COUNT(*) FROM DurableEvents WHERE Sequence > $ack;",
                        cancellationToken,
                        ("$ack", acknowledged)),
                    CultureInfo.InvariantCulture)
                : 0;
            long totalEvents = hasDurableEvents
                ? Convert.ToInt64(
                    await ScalarAsync(connection, "SELECT COUNT(*) FROM DurableEvents;", cancellationToken),
                    CultureInfo.InvariantCulture)
                : 0;
            bool activeCall = hasActiveCallJournal &&
                Convert.ToInt64(
                    await ScalarAsync(connection, "SELECT COUNT(*) FROM ActiveCallJournal;", cancellationToken),
                    CultureInfo.InvariantCulture) > 0;

            bool requiresManualMigration = schemaVersion <= 2 && (totalEvents > 0 || activeCall);
            return requiresManualMigration
                ? new(schemaVersion, pending, activeCall, false, "collection_binding_migration_required")
                : new(schemaVersion, pending, activeCall, true, "storage_ready");
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(0, 0, false, false, "outbox_unavailable");
        }
    }

    public static int ExitCode(StoragePreflightResult result) => result.SafeToUpgrade
        ? 0
        : result.Code == "collection_binding_migration_required"
            ? MigrationRequiredExitCode
            : StorageUnavailableExitCode;

    public static string Serialize(StoragePreflightResult result) => JsonSerializer.Serialize(
        result,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken) => Convert.ToInt64(
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;",
                cancellationToken,
                ("$name", table)),
            CultureInfo.InvariantCulture) != 0;

    private static async Task<bool> HasExpectedSchemaAsync(
        SqliteConnection connection,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        var queries = new List<string>
        {
            "SELECT MetadataKey, MetadataValue FROM AgentMetadata LIMIT 0;",
            "SELECT Sequence, EventId, AgentSessionId, CallId, CommandId, EventType, OccurredAtUtc, CallState, DataJson FROM DurableEvents LIMIT 0;",
            schemaVersion >= 2
                ? "SELECT CommandId, CommandType, RequestHash, ResultJson, ProcessedAtUtc, ExecutionState FROM ProcessedCommands LIMIT 0;"
                : "SELECT CommandId, CommandType, RequestHash, ResultJson, ProcessedAtUtc FROM ProcessedCommands LIMIT 0;"
        };
        if (schemaVersion >= 2)
        {
            string remotePartyColumn = schemaVersion >= 4 ? "RemoteParty" : "MaskedRemoteParty";
            queries.Add($"SELECT JournalId, CallId, CommandId, Direction, CallState, StartedAtUtc, AnsweredAtUtc, EndedAtUtc, Outcome, EndReason, {remotePartyColumn} FROM ActiveCallJournal LIMIT 0;");
        }
        if (schemaVersion >= 3)
        {
            queries.Add("SELECT CollectionId, CollectionBindingId, CallContextId FROM DurableEvents LIMIT 0;");
            queries.Add("SELECT CollectionId, CollectionBindingId, CallContextId FROM ActiveCallJournal LIMIT 0;");
        }

        try
        {
            foreach (string query in queries)
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = query;
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            }

            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
