using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Persistence;
using Microsoft.Data.Sqlite;

namespace DebtFlow.SipAgent.Core.Tests;

public sealed class SqliteAgentEventStoreTests
{
    [Fact]
    public async Task Reopen_PreservesIdentitySequenceAndPendingReplay()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        try
        {
            string instanceId;
            await using (var first = new SqliteAgentEventStore(path))
            {
                await first.InitializeAsync(CancellationToken.None);
                instanceId = first.AgentInstanceId;
                StoredDurableEvent stored = await first.AppendAsync(CreateDraft(), CancellationToken.None);
                Assert.Equal(1, stored.Sequence);
            }

            await using var reopened = new SqliteAgentEventStore(path);
            await reopened.InitializeAsync(CancellationToken.None);
            Assert.Equal(instanceId, reopened.AgentInstanceId);
            Assert.Equal(1, reopened.LastSequence);
            IReadOnlyList<StoredDurableEvent> pending = await reopened.LoadPendingAsync(0, 100, CancellationToken.None);
            Assert.Single(pending);
            Assert.Equal(1, pending[0].Sequence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Acknowledge_PurgesContiguousEventsAndSurvivesReopen()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        try
        {
            await using (var store = new SqliteAgentEventStore(path))
            {
                await store.InitializeAsync(CancellationToken.None);
                await store.AppendAsync(CreateDraft(), CancellationToken.None);
                await store.AppendAsync(CreateDraft(), CancellationToken.None);
                await store.AcknowledgeThroughAsync(2, CancellationToken.None);
                Assert.Equal(0, await store.CountPendingAsync(CancellationToken.None));
            }

            await using var reopened = new SqliteAgentEventStore(path);
            await reopened.InitializeAsync(CancellationToken.None);
            Assert.Equal(2, reopened.LastAcknowledgedSequence);
            StoredDurableEvent next = await reopened.AppendAsync(CreateDraft(), CancellationToken.None);
            Assert.Equal(3, next.Sequence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CachedHealth_TracksPendingCountAndOldestEventAcrossAcknowledgements()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        DateTimeOffset firstAt = DateTimeOffset.Parse("2026-10-06T01:00:00Z");
        DateTimeOffset secondAt = firstAt.AddSeconds(5);
        try
        {
            await using var store = new SqliteAgentEventStore(path);
            await store.InitializeAsync(CancellationToken.None);
            await store.AppendAsync(CreateDraft() with { OccurredAtUtc = firstAt }, CancellationToken.None);
            await store.AppendAsync(CreateDraft() with { OccurredAtUtc = secondAt }, CancellationToken.None);

            EventStoreHealth initial = await store.GetHealthAsync(CancellationToken.None);
            Assert.Equal(2, initial.PendingEventCount);
            Assert.Equal(firstAt, initial.OldestPendingAtUtc);
            Assert.True(initial.StorageBytes > 0);

            await store.AcknowledgeThroughAsync(1, CancellationToken.None);
            EventStoreHealth afterFirstAck = await store.GetHealthAsync(CancellationToken.None);
            Assert.Equal(1, afterFirstAck.PendingEventCount);
            Assert.Equal(secondAt, afterFirstAck.OldestPendingAtUtc);

            await store.AcknowledgeThroughAsync(2, CancellationToken.None);
            EventStoreHealth afterSecondAck = await store.GetHealthAsync(CancellationToken.None);
            Assert.Equal(0, afterSecondAck.PendingEventCount);
            Assert.Null(afterSecondAck.OldestPendingAtUtc);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProcessedCommand_CanBeUpdatedWithoutCreatingDuplicate()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await using var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
            await store.InitializeAsync(CancellationToken.None);
            var provisional = new ProcessedCommand("command", "call.start", "hash", "unknown", DateTimeOffset.UtcNow);
            await store.SaveCommandAsync(provisional, CancellationToken.None);
            await store.SaveCommandAsync(provisional with { ResultJson = "accepted" }, CancellationToken.None);

            ProcessedCommand? result = await store.FindCommandAsync("command", CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal("accepted", result.ResultJson);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverExecutingCommands_MarksInterruptedCommandAbandonedForDeterministicReplay()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await using var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
            await store.InitializeAsync(CancellationToken.None);
            DateTimeOffset recoveredAt = DateTimeOffset.Parse("2026-10-06T01:02:03Z");
            await store.SaveCommandAsync(
                new ProcessedCommand(
                    "interrupted",
                    "call.start",
                    "v2:hash",
                    "{\"errorCode\":\"command_outcome_unknown\"}",
                    recoveredAt.AddMinutes(-1),
                    "executing"),
                CancellationToken.None);

            await store.RecoverExecutingCommandsAsync(recoveredAt, CancellationToken.None);

            ProcessedCommand? recovered = await store.FindCommandAsync("interrupted", CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal("abandoned", recovered.ExecutionState);
            Assert.Equal(recoveredAt, recovered.ProcessedAtUtc);
            Assert.Contains("command_outcome_unknown", recovered.ResultJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Initialize_MigratesV1AndPreservesExecutingCommandIntent()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        try
        {
            await CreateVersionOneDatabaseAsync(path);

            await using var store = new SqliteAgentEventStore(path);
            await store.InitializeAsync(CancellationToken.None);

            ProcessedCommand? command = await store.FindCommandAsync("command-v1", CancellationToken.None);
            Assert.NotNull(command);
            Assert.Equal("executing", command.ExecutionState);
            Assert.Null(await store.LoadActiveCallAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Initialize_MigratesEmptyV2DatabaseToCollectionBindingSchema()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        try
        {
            await CreateVersionTwoDatabaseAsync(path, includePendingEvent: false);

            await using var store = new SqliteAgentEventStore(path);
            await store.InitializeAsync(CancellationToken.None);
            StoredDurableEvent stored = await store.AppendAsync(
                CreateDraft() with
                {
                    CollectionId = "collection-test",
                    CollectionBindingId = $"phonebind_{Guid.NewGuid():N}",
                    CallContextId = $"phonectx_{Guid.NewGuid():N}"
                },
                CancellationToken.None);

            Assert.Equal("collection-test", stored.CollectionId);
            Assert.NotNull(stored.CollectionBindingId);
            Assert.NotNull(stored.CallContextId);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Initialize_V2DatabaseWithPendingEvent_FailsClosedForBindingMigration()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        try
        {
            await CreateVersionTwoDatabaseAsync(path, includePendingEvent: true);

            await using var store = new SqliteAgentEventStore(path);
            AgentStoreException exception = await Assert.ThrowsAsync<AgentStoreException>(
                () => store.InitializeAsync(CancellationToken.None));

            Assert.Equal("collection_binding_migration_required", exception.ErrorCode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Initialize_MigratesV3RemotePartyColumnWithoutInventingLegacyDigits()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        try
        {
            await CreateVersionThreeDatabaseAsync(path);

            await using var store = new SqliteAgentEventStore(path);
            await store.InitializeAsync(CancellationToken.None);
            CallSessionState call = Assert.IsType<CallSessionState>(
                await store.LoadActiveCallAsync(CancellationToken.None));

            Assert.Equal("xxxxxx5678", call.RemoteParty);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Initialize_RejectsNewerSchemaWithoutReplacingDatabase()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Sentinel(Value TEXT); INSERT INTO Sentinel VALUES ('keep'); PRAGMA user_version=99;";
                await command.ExecuteNonQueryAsync();
            }

            await using var store = new SqliteAgentEventStore(path);
            AgentStoreException exception = await Assert.ThrowsAsync<AgentStoreException>(
                () => store.InitializeAsync(CancellationToken.None));
            Assert.Equal("outbox_schema_newer", exception.ErrorCode);

            await using var reopened = new SqliteConnection($"Data Source={path}");
            await reopened.OpenAsync();
            await using SqliteCommand verify = reopened.CreateCommand();
            verify.CommandText = "SELECT Value FROM Sentinel;";
            Assert.Equal("keep", await verify.ExecuteScalarAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Initialize_CorruptDatabaseFailsClosedAndPreservesOriginalBytes()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        byte[] original = "not-a-sqlite-database"u8.ToArray();
        try
        {
            await File.WriteAllBytesAsync(path, original);
            await using var store = new SqliteAgentEventStore(path);

            AgentStoreException exception = await Assert.ThrowsAsync<AgentStoreException>(
                () => store.InitializeAsync(CancellationToken.None));

            Assert.Equal("outbox_corrupt", exception.ErrorCode);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PruneCommands_RetainsExecutingAndPendingEventReferences()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await using var store = new SqliteAgentEventStore(Path.Combine(directory, "agent.db"));
            await store.InitializeAsync(CancellationToken.None);
            DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-60);
            await store.SaveCommandAsync(
                new ProcessedCommand("remove", "state.get", "hash", "{}", old),
                CancellationToken.None);
            await store.SaveCommandAsync(
                new ProcessedCommand("executing", "call.start", "hash", "{}", old, "executing"),
                CancellationToken.None);
            await store.SaveCommandAsync(
                new ProcessedCommand("protected", "call.start", "hash", "{}", old),
                CancellationToken.None);
            await store.AppendAsync(CreateDraft(commandId: "protected"), CancellationToken.None);

            await store.PruneCommandsAsync(DateTimeOffset.UtcNow.AddDays(-30), 100, CancellationToken.None);

            Assert.Null(await store.FindCommandAsync("remove", CancellationToken.None));
            Assert.NotNull(await store.FindCommandAsync("executing", CancellationToken.None));
            Assert.NotNull(await store.FindCommandAsync("protected", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CallJournal_PersistsNonTerminalCallAndClearsWithTerminalEvent()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "agent.db");
        string callId = Guid.NewGuid().ToString("D");
        var active = new CallSessionState(
            callId,
            Guid.NewGuid().ToString("D"),
            CallDirection.Outbound,
            CallState.Ringing,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null,
            "0812345678");
        try
        {
            await using (var store = new SqliteAgentEventStore(path))
            {
                await store.InitializeAsync(CancellationToken.None);
                await store.AppendCallEventAsync(CreateDraft(callId), active, false, CancellationToken.None);
            }

            await using (var reopened = new SqliteAgentEventStore(path))
            {
                await reopened.InitializeAsync(CancellationToken.None);
                CallSessionState recovered = Assert.IsType<CallSessionState>(
                    await reopened.LoadActiveCallAsync(CancellationToken.None));
                Assert.Equal(CallState.Ringing, recovered.State);

                CallSessionState ended = recovered with
                {
                    State = CallState.Ended,
                    EndedAtUtc = DateTimeOffset.UtcNow,
                    Outcome = CallOutcome.Cancelled,
                    EndReason = "local_hangup"
                };
                await reopened.AppendCallEventAsync(CreateDraft(callId), ended, true, CancellationToken.None);
                Assert.Null(await reopened.LoadActiveCallAsync(CancellationToken.None));
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Capacity_ReservesFinalTenPercentForExistingCallEvents()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            await using var store = new SqliteAgentEventStore(
                Path.Combine(directory, "agent.db"),
                new EventStoreLimits(10, 1024L * 1024 * 1024));
            await store.InitializeAsync(CancellationToken.None);
            for (int index = 0; index < 8; index++)
            {
                await store.AppendAsync(CreateDraft(), CancellationToken.None);
            }

            Assert.Equal(EventStoreCapacityState.Warning, (await store.GetHealthAsync(CancellationToken.None)).CapacityState);
            await store.AppendAsync(CreateDraft(), CancellationToken.None);
            Assert.Equal(EventStoreCapacityState.Critical, (await store.GetHealthAsync(CancellationToken.None)).CapacityState);

            AgentStoreException blocked = await Assert.ThrowsAsync<AgentStoreException>(
                () => store.AppendAsync(CreateDraft(), CancellationToken.None));
            Assert.Equal("outbox_capacity_critical", blocked.ErrorCode);

            var active = new CallSessionState(
                Guid.NewGuid().ToString("D"),
                null,
                CallDirection.Inbound,
                CallState.Incoming,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null,
                "0812341234");
            await store.AppendCallEventAsync(CreateDraft(active.CallId), active, false, CancellationToken.None);
            Assert.Equal(EventStoreCapacityState.Full, (await store.GetHealthAsync(CancellationToken.None)).CapacityState);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static DurableEventDraft CreateDraft(string? callId = null, string? commandId = null) => new(
        Guid.NewGuid().ToString("D"),
        Guid.NewGuid().ToString("D"),
        callId ?? Guid.NewGuid().ToString("D"),
        commandId ?? Guid.NewGuid().ToString("D"),
        "call.state_changed",
        DateTimeOffset.UtcNow,
        "ringing",
        "{}");

    private static async Task CreateVersionOneDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE AgentMetadata (
                MetadataKey TEXT NOT NULL PRIMARY KEY,
                MetadataValue TEXT NOT NULL
            );
            CREATE TABLE DurableEvents (
                Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE,
                AgentSessionId TEXT NOT NULL,
                CallId TEXT NOT NULL,
                CommandId TEXT NULL,
                EventType TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                CallState TEXT NULL,
                DataJson TEXT NOT NULL
            );
            CREATE TABLE ProcessedCommands (
                CommandId TEXT NOT NULL PRIMARY KEY,
                CommandType TEXT NOT NULL,
                RequestHash TEXT NOT NULL,
                ResultJson TEXT NOT NULL,
                ProcessedAtUtc TEXT NOT NULL
            );
            INSERT INTO ProcessedCommands
                (CommandId, CommandType, RequestHash, ResultJson, ProcessedAtUtc)
            VALUES
                ('command-v1', 'call.start', 'hash', '{"errorCode":"command_outcome_unknown"}', '2026-10-01T00:00:00.0000000+00:00');
            PRAGMA user_version=1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateVersionTwoDatabaseAsync(string path, bool includePendingEvent)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE AgentMetadata (
                MetadataKey TEXT NOT NULL PRIMARY KEY,
                MetadataValue TEXT NOT NULL
            );
            CREATE TABLE DurableEvents (
                Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE,
                AgentSessionId TEXT NOT NULL,
                CallId TEXT NOT NULL,
                CommandId TEXT NULL,
                EventType TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                CallState TEXT NULL,
                DataJson TEXT NOT NULL
            );
            CREATE TABLE ProcessedCommands (
                CommandId TEXT NOT NULL PRIMARY KEY,
                CommandType TEXT NOT NULL,
                RequestHash TEXT NOT NULL,
                ResultJson TEXT NOT NULL,
                ProcessedAtUtc TEXT NOT NULL,
                ExecutionState TEXT NOT NULL
            );
            CREATE TABLE ActiveCallJournal (
                JournalId INTEGER NOT NULL PRIMARY KEY CHECK (JournalId = 1),
                CallId TEXT NOT NULL,
                CommandId TEXT NULL,
                Direction TEXT NOT NULL,
                CallState TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                AnsweredAtUtc TEXT NULL,
                EndedAtUtc TEXT NULL,
                Outcome TEXT NULL,
                EndReason TEXT NULL,
                MaskedRemoteParty TEXT NOT NULL
            );
            CREATE INDEX IX_ProcessedCommands_ProcessedAtUtc ON ProcessedCommands (ProcessedAtUtc);
            PRAGMA user_version=2;
            """;
        if (includePendingEvent)
        {
            command.CommandText +=
                """
                INSERT INTO DurableEvents
                    (EventId, AgentSessionId, CallId, CommandId, EventType, OccurredAtUtc, CallState, DataJson)
                VALUES
                    ('event-v2', 'session-v2', 'call-v2', NULL, 'call.created',
                     '2026-10-01T00:00:00.0000000+00:00', 'created', '{}');
                """;
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateVersionThreeDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE AgentMetadata (
                MetadataKey TEXT NOT NULL PRIMARY KEY,
                MetadataValue TEXT NOT NULL
            );
            CREATE TABLE DurableEvents (
                Sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE,
                AgentSessionId TEXT NOT NULL,
                CallId TEXT NOT NULL,
                CommandId TEXT NULL,
                EventType TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                CallState TEXT NULL,
                DataJson TEXT NOT NULL,
                CollectionId TEXT NULL,
                CollectionBindingId TEXT NULL,
                CallContextId TEXT NULL
            );
            CREATE TABLE ProcessedCommands (
                CommandId TEXT NOT NULL PRIMARY KEY,
                CommandType TEXT NOT NULL,
                RequestHash TEXT NOT NULL,
                ResultJson TEXT NOT NULL,
                ProcessedAtUtc TEXT NOT NULL,
                ExecutionState TEXT NOT NULL
            );
            CREATE TABLE ActiveCallJournal (
                JournalId INTEGER NOT NULL PRIMARY KEY CHECK (JournalId = 1),
                CallId TEXT NOT NULL,
                CommandId TEXT NULL,
                Direction TEXT NOT NULL,
                CallState TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                AnsweredAtUtc TEXT NULL,
                EndedAtUtc TEXT NULL,
                Outcome TEXT NULL,
                EndReason TEXT NULL,
                MaskedRemoteParty TEXT NOT NULL,
                CollectionId TEXT NULL,
                CollectionBindingId TEXT NULL,
                CallContextId TEXT NULL
            );
            INSERT INTO ActiveCallJournal
                (JournalId, CallId, Direction, CallState, StartedAtUtc, MaskedRemoteParty)
            VALUES
                (1, 'call-v3', 'Outbound', 'Ringing', '2026-10-01T00:00:00.0000000+00:00',
                 'xxxxxx5678');
            CREATE INDEX IX_ProcessedCommands_ProcessedAtUtc ON ProcessedCommands (ProcessedAtUtc);
            PRAGMA user_version=3;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sip-agent-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
