using Microsoft.Data.Sqlite;
using DebtFlow.SipAgent.Persistence;

namespace DebtFlow.SipAgent.Host.Tests;

public sealed class StoragePreflightTests
{
    [Fact]
    public async Task MissingDatabase_IsSafeAndDoesNotCreateStorage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.db");

        StoragePreflightResult result = await StoragePreflight.InspectAsync(path, CancellationToken.None);

        Assert.True(result.SafeToUpgrade);
        Assert.Equal("storage_not_created", result.Code);
        Assert.Equal(0, StoragePreflight.ExitCode(result));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CurrentDatabase_WithPendingEvents_IsSafeAndReportedWithoutMutation()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "agent.db");
        try
        {
            await using (var store = new SqliteAgentEventStore(path))
            {
                await store.InitializeAsync(CancellationToken.None);
                await store.AppendAsync(new(
                    Guid.NewGuid().ToString("D"),
                    Guid.NewGuid().ToString("D"),
                    Guid.NewGuid().ToString("D"),
                    null,
                    "call.created",
                    DateTimeOffset.UtcNow,
                    "created",
                    "{}"), CancellationToken.None);
            }

            StoragePreflightResult result = await StoragePreflight.InspectAsync(path, CancellationToken.None);

            Assert.True(result.SafeToUpgrade);
            Assert.Equal(AgentReleaseMetadata.StorageSchemaVersion, result.SchemaVersion);
            Assert.Equal(1, result.PendingEvents);
            Assert.Equal("storage_ready", result.Code);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LegacyV2Database_WithEvent_RequiresManualMigration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"sip-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "agent.db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE AgentMetadata (MetadataKey TEXT PRIMARY KEY, MetadataValue TEXT NOT NULL);
                CREATE TABLE DurableEvents (
                    Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    EventId TEXT, AgentSessionId TEXT, CallId TEXT, CommandId TEXT,
                    EventType TEXT, OccurredAtUtc TEXT, CallState TEXT, DataJson TEXT);
                CREATE TABLE ProcessedCommands (
                    CommandId TEXT PRIMARY KEY, CommandType TEXT, RequestHash TEXT,
                    ResultJson TEXT, ProcessedAtUtc TEXT, ExecutionState TEXT);
                CREATE TABLE ActiveCallJournal (
                    JournalId INTEGER PRIMARY KEY, CallId TEXT, CommandId TEXT, Direction TEXT,
                    CallState TEXT, StartedAtUtc TEXT, AnsweredAtUtc TEXT, EndedAtUtc TEXT,
                    Outcome TEXT, EndReason TEXT, MaskedRemoteParty TEXT);
                INSERT INTO DurableEvents
                    (EventId, AgentSessionId, CallId, EventType, OccurredAtUtc, DataJson)
                VALUES ('event', 'session', 'call', 'call.created', '2026-10-06T00:00:00Z', '{}');
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync();
            await connection.CloseAsync();

            StoragePreflightResult result = await StoragePreflight.InspectAsync(path, CancellationToken.None);

            Assert.False(result.SafeToUpgrade);
            Assert.Equal("collection_binding_migration_required", result.Code);
            Assert.Equal(StoragePreflight.MigrationRequiredExitCode, StoragePreflight.ExitCode(result));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
