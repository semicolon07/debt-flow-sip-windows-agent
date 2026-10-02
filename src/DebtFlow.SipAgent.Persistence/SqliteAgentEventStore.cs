using System.Globalization;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;
using Microsoft.Data.Sqlite;

namespace DebtFlow.SipAgent.Persistence;

public sealed record EventStoreLimits(long MaximumPendingEvents, long MaximumStorageBytes)
{
    public static EventStoreLimits Default { get; } = new(10_000, 100L * 1024 * 1024);
}

public sealed class SqliteAgentEventStore : IAgentEventStore
{
    private const int CurrentSchemaVersion = 3;
    private readonly string _databasePath;
    private readonly EventStoreLimits _limits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public SqliteAgentEventStore(string databasePath, EventStoreLimits? limits = null)
    {
        _databasePath = databasePath;
        _limits = limits ?? EventStoreLimits.Default;
        if (_limits.MaximumPendingEvents < 10 || _limits.MaximumStorageBytes < 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    public string AgentInstanceId { get; private set; } = string.Empty;
    public long LastSequence { get; private set; }
    public long LastAcknowledgedSequence { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection != null)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString());

            await connection.OpenAsync(cancellationToken);
            _connection = connection;
            await ExecuteNonQueryAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            await ExecuteNonQueryAsync("PRAGMA synchronous=FULL;", cancellationToken);
            await ExecuteNonQueryAsync("PRAGMA busy_timeout=5000;", cancellationToken);
            await ExecuteNonQueryAsync("PRAGMA foreign_keys=ON;", cancellationToken);
            await MigrateAsync(cancellationToken);
            await ValidateIntegrityAsync(cancellationToken);

            AgentInstanceId = await ReadMetadataAsync("agent_instance_id", cancellationToken)
                ?? ProtocolCodec.NewId();
            await WriteMetadataAsync("agent_instance_id", AgentInstanceId, cancellationToken);
            string? acknowledged = await ReadMetadataAsync("last_acknowledged_sequence", cancellationToken);
            LastAcknowledgedSequence = long.TryParse(
                acknowledged,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long ack)
                ? ack
                : 0;

            await using SqliteCommand sequenceCommand = connection.CreateCommand();
            sequenceCommand.CommandText = "SELECT COALESCE(MAX(Sequence), $ack) FROM DurableEvents;";
            sequenceCommand.Parameters.AddWithValue("$ack", LastAcknowledgedSequence);
            LastSequence = Convert.ToInt64(
                await sequenceCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            await ValidateSequenceInvariantAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            throw NormalizeStoreException(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<StoredDurableEvent> AppendAsync(
        DurableEventDraft draft,
        CancellationToken cancellationToken) =>
        AppendInternalAsync(draft, null, false, useReservedCapacity: false, cancellationToken);

    public Task<StoredDurableEvent> AppendCallEventAsync(
        DurableEventDraft draft,
        CallSessionState callState,
        bool terminal,
        CancellationToken cancellationToken) =>
        AppendInternalAsync(draft, callState, terminal, useReservedCapacity: true, cancellationToken);

    public async Task<IReadOnlyList<StoredDurableEvent>> LoadPendingAsync(
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteCommand command = RequireConnection().CreateCommand();
            command.CommandText =
                """
                SELECT Sequence, EventId, AgentSessionId, CallId, CommandId, EventType,
                       OccurredAtUtc, CallState, DataJson, CollectionId, CollectionBindingId, CallContextId
                FROM DurableEvents
                WHERE Sequence > $afterSequence
                ORDER BY Sequence
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$afterSequence", Math.Max(LastAcknowledgedSequence, afterSequence));
            command.Parameters.AddWithValue("$limit", maximumCount);
            var results = new List<StoredDurableEvent>();
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(ReadStoredEvent(reader));
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> CountPendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await CountPendingCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EventStoreHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GetHealthCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CallSessionState?> LoadActiveCallAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteCommand command = RequireConnection().CreateCommand();
            command.CommandText =
                """
                SELECT CallId, CommandId, Direction, CallState, StartedAtUtc, AnsweredAtUtc,
                       EndedAtUtc, Outcome, EndReason, MaskedRemoteParty,
                       CollectionId, CollectionBindingId, CallContextId
                FROM ActiveCallJournal
                WHERE JournalId = 1;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            if (!Enum.TryParse(reader.GetString(2), false, out CallDirection direction) ||
                !Enum.TryParse(reader.GetString(3), false, out CallState state))
            {
                throw new AgentStoreException("outbox_invariant_invalid");
            }

            CallOutcome? outcome = null;
            if (!reader.IsDBNull(7))
            {
                if (!Enum.TryParse(reader.GetString(7), false, out CallOutcome parsedOutcome))
                {
                    throw new AgentStoreException("outbox_invariant_invalid");
                }

                outcome = parsedOutcome;
            }

            return new CallSessionState(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                direction,
                state,
                ParseTimestamp(reader.GetString(4)),
                reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
                reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                outcome,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcknowledgeThroughAsync(long sequence, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (sequence <= LastAcknowledgedSequence)
            {
                return;
            }

            if (sequence > LastSequence)
            {
                throw new InvalidOperationException("ack_sequence_not_emitted");
            }

            SqliteConnection connection = RequireConnection();
            await using SqliteCommand countCommand = connection.CreateCommand();
            countCommand.CommandText =
                "SELECT COUNT(*) FROM DurableEvents WHERE Sequence > $ack AND Sequence <= $sequence;";
            countCommand.Parameters.AddWithValue("$ack", LastAcknowledgedSequence);
            countCommand.Parameters.AddWithValue("$sequence", sequence);
            long expected = sequence - LastAcknowledgedSequence;
            long actual = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (actual != expected)
            {
                throw new InvalidOperationException("ack_sequence_gap");
            }

            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using SqliteCommand metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText =
                """
                INSERT INTO AgentMetadata (MetadataKey, MetadataValue)
                VALUES ('last_acknowledged_sequence', $value)
                ON CONFLICT(MetadataKey) DO UPDATE SET MetadataValue = excluded.MetadataValue;
                """;
            metadata.Parameters.AddWithValue("$value", sequence.ToString(CultureInfo.InvariantCulture));
            await metadata.ExecuteNonQueryAsync(cancellationToken);
            await using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM DurableEvents WHERE Sequence <= $sequence;";
            delete.Parameters.AddWithValue("$sequence", sequence);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            LastAcknowledgedSequence = sequence;
            await CheckpointCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProcessedCommand?> FindCommandAsync(string commandId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteCommand command = RequireConnection().CreateCommand();
            command.CommandText =
                """
                SELECT CommandId, CommandType, RequestHash, ResultJson, ProcessedAtUtc, ExecutionState
                FROM ProcessedCommands
                WHERE CommandId = $commandId;
                """;
            command.Parameters.AddWithValue("$commandId", commandId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new ProcessedCommand(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseTimestamp(reader.GetString(4)),
                reader.GetString(5));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveCommandAsync(ProcessedCommand command, CancellationToken cancellationToken)
    {
        if (command.ExecutionState is not "executing" and not "completed" and not "failed")
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteCommand sqliteCommand = RequireConnection().CreateCommand();
            sqliteCommand.CommandText =
                """
                INSERT INTO ProcessedCommands
                    (CommandId, CommandType, RequestHash, ResultJson, ProcessedAtUtc, ExecutionState)
                VALUES
                    ($commandId, $commandType, $requestHash, $resultJson, $processedAtUtc, $executionState)
                ON CONFLICT(CommandId) DO UPDATE SET
                    CommandType = excluded.CommandType,
                    RequestHash = excluded.RequestHash,
                    ResultJson = excluded.ResultJson,
                    ProcessedAtUtc = excluded.ProcessedAtUtc,
                    ExecutionState = excluded.ExecutionState;
                """;
            sqliteCommand.Parameters.AddWithValue("$commandId", command.CommandId);
            sqliteCommand.Parameters.AddWithValue("$commandType", command.CommandType);
            sqliteCommand.Parameters.AddWithValue("$requestHash", command.RequestHash);
            sqliteCommand.Parameters.AddWithValue("$resultJson", command.ResultJson);
            sqliteCommand.Parameters.AddWithValue(
                "$processedAtUtc",
                command.ProcessedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            sqliteCommand.Parameters.AddWithValue("$executionState", command.ExecutionState);
            await sqliteCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PruneCommandsAsync(
        DateTimeOffset olderThanUtc,
        int maximumRetained,
        CancellationToken cancellationToken)
    {
        if (maximumRetained < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetained));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteCommand command = RequireConnection().CreateCommand();
            command.CommandText =
                """
                DELETE FROM ProcessedCommands
                WHERE ExecutionState != 'executing'
                  AND CommandId NOT IN (
                      SELECT CommandId FROM DurableEvents WHERE CommandId IS NOT NULL
                      UNION
                      SELECT CommandId FROM ActiveCallJournal WHERE CommandId IS NOT NULL)
                  AND (
                      ProcessedAtUtc < $olderThanUtc
                      OR CommandId IN (
                          SELECT CommandId
                          FROM ProcessedCommands
                          WHERE ExecutionState != 'executing'
                          ORDER BY ProcessedAtUtc DESC
                          LIMIT -1 OFFSET $maximumRetained));
                """;
            command.Parameters.AddWithValue(
                "$olderThanUtc",
                olderThanUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$maximumRetained", maximumRetained);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CheckpointCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_connection != null)
            {
                try
                {
                    await CheckpointCoreAsync(CancellationToken.None);
                }
                catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
                {
                }

                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<StoredDurableEvent> AppendInternalAsync(
        DurableEventDraft draft,
        CallSessionState? callState,
        bool terminal,
        bool useReservedCapacity,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EventStoreHealth health = await GetHealthCoreAsync(cancellationToken);
            if (health.CapacityState == EventStoreCapacityState.Full ||
                !useReservedCapacity && health.CapacityState == EventStoreCapacityState.Critical)
            {
                throw new AgentStoreException(
                    health.CapacityState == EventStoreCapacityState.Full
                        ? "outbox_capacity_exceeded"
                        : "outbox_capacity_critical");
            }

            SqliteConnection connection = RequireConnection();
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            long sequence = await InsertEventAsync(connection, transaction, draft, cancellationToken);
            if (callState != null)
            {
                if (terminal)
                {
                    await DeleteActiveCallAsync(connection, transaction, callState.CallId, cancellationToken);
                }
                else
                {
                    await UpsertActiveCallAsync(connection, transaction, callState, cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            LastSequence = Math.Max(LastSequence, sequence);
            return new StoredDurableEvent(
                sequence,
                draft.EventId,
                AgentInstanceId,
                draft.AgentSessionId,
                draft.CallId,
                draft.CommandId,
                draft.EventType,
                draft.OccurredAtUtc,
                draft.State,
                draft.DataJson,
                draft.CollectionId,
                draft.CollectionBindingId,
                draft.CallContextId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<long> InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableEventDraft draft,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO DurableEvents
                (EventId, AgentSessionId, CallId, CommandId, EventType, OccurredAtUtc, CallState, DataJson,
                 CollectionId, CollectionBindingId, CallContextId)
            VALUES
                ($eventId, $agentSessionId, $callId, $commandId, $eventType, $occurredAtUtc, $callState, $dataJson,
                 $collectionId, $collectionBindingId, $callContextId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$eventId", draft.EventId);
        command.Parameters.AddWithValue("$agentSessionId", draft.AgentSessionId);
        command.Parameters.AddWithValue("$callId", draft.CallId);
        command.Parameters.AddWithValue("$commandId", (object?)draft.CommandId ?? DBNull.Value);
        command.Parameters.AddWithValue("$eventType", draft.EventType);
        command.Parameters.AddWithValue("$occurredAtUtc", FormatTimestamp(draft.OccurredAtUtc));
        command.Parameters.AddWithValue("$callState", (object?)draft.State ?? DBNull.Value);
        command.Parameters.AddWithValue("$dataJson", draft.DataJson);
        command.Parameters.AddWithValue("$collectionId", (object?)draft.CollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$collectionBindingId", (object?)draft.CollectionBindingId ?? DBNull.Value);
        command.Parameters.AddWithValue("$callContextId", (object?)draft.CallContextId ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task UpsertActiveCallAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CallSessionState call,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ActiveCallJournal
                (JournalId, CallId, CommandId, Direction, CallState, StartedAtUtc, AnsweredAtUtc,
                 EndedAtUtc, Outcome, EndReason, MaskedRemoteParty, CollectionId, CollectionBindingId, CallContextId)
            VALUES
                (1, $callId, $commandId, $direction, $callState, $startedAtUtc, $answeredAtUtc,
                 $endedAtUtc, $outcome, $endReason, $maskedRemoteParty, $collectionId, $collectionBindingId, $callContextId)
            ON CONFLICT(JournalId) DO UPDATE SET
                CallId = excluded.CallId,
                CommandId = excluded.CommandId,
                Direction = excluded.Direction,
                CallState = excluded.CallState,
                StartedAtUtc = excluded.StartedAtUtc,
                AnsweredAtUtc = excluded.AnsweredAtUtc,
                EndedAtUtc = excluded.EndedAtUtc,
                Outcome = excluded.Outcome,
                EndReason = excluded.EndReason,
                MaskedRemoteParty = excluded.MaskedRemoteParty,
                CollectionId = excluded.CollectionId,
                CollectionBindingId = excluded.CollectionBindingId,
                CallContextId = excluded.CallContextId;
            """;
        command.Parameters.AddWithValue("$callId", call.CallId);
        command.Parameters.AddWithValue("$commandId", (object?)call.CommandId ?? DBNull.Value);
        command.Parameters.AddWithValue("$direction", call.Direction.ToString());
        command.Parameters.AddWithValue("$callState", call.State.ToString());
        command.Parameters.AddWithValue("$startedAtUtc", FormatTimestamp(call.StartedAtUtc));
        command.Parameters.AddWithValue("$answeredAtUtc", FormatNullableTimestamp(call.AnsweredAtUtc));
        command.Parameters.AddWithValue("$endedAtUtc", FormatNullableTimestamp(call.EndedAtUtc));
        command.Parameters.AddWithValue("$outcome", call.Outcome?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$endReason", (object?)call.EndReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$maskedRemoteParty", call.MaskedRemoteParty);
        command.Parameters.AddWithValue("$collectionId", (object?)call.CollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$collectionBindingId", (object?)call.CollectionBindingId ?? DBNull.Value);
        command.Parameters.AddWithValue("$callContextId", (object?)call.CallContextId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteActiveCallAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string callId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ActiveCallJournal WHERE JournalId = 1 AND CallId = $callId;";
        command.Parameters.AddWithValue("$callId", callId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = RequireConnection();
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        int version = Convert.ToInt32(
            await versionCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            throw new AgentStoreException("outbox_schema_newer");
        }

        if (version == CurrentSchemaVersion)
        {
            return;
        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (version == 0)
        {
            await ExecuteMigrationCommandAsync(connection, transaction, CreateSchemaSql, cancellationToken);
        }
        else
        {
            if (version == 1)
            {
                await ExecuteMigrationCommandAsync(connection, transaction, UpgradeV1ToV2Sql, cancellationToken);
                version = 2;
            }

            if (version == 2)
            {
                await EnsureCollectionBindingMigrationSafeAsync(connection, transaction, cancellationToken);
                await ExecuteMigrationCommandAsync(connection, transaction, UpgradeV2ToV3Sql, cancellationToken);
            }
        }

        await ExecuteMigrationCommandAsync(
            connection,
            transaction,
            $"PRAGMA user_version={CurrentSchemaVersion};",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureCollectionBindingMigrationSafeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM DurableEvents) + (SELECT COUNT(*) FROM ActiveCallJournal);";
        long rows = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (rows != 0)
        {
            throw new AgentStoreException("collection_binding_migration_required");
        }
    }

    private static async Task ExecuteMigrationCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ValidateIntegrityAsync(CancellationToken cancellationToken)
    {
        await using SqliteCommand command = RequireConnection().CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        object? scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(Convert.ToString(scalar, CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal))
        {
            throw new AgentStoreException("outbox_corrupt");
        }
    }

    private async Task ValidateSequenceInvariantAsync(CancellationToken cancellationToken)
    {
        if (LastAcknowledgedSequence < 0 || LastSequence < LastAcknowledgedSequence)
        {
            throw new AgentStoreException("outbox_invariant_invalid");
        }

        await using SqliteCommand command = RequireConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DurableEvents WHERE Sequence > $ack;";
        command.Parameters.AddWithValue("$ack", LastAcknowledgedSequence);
        long actual = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (actual != LastSequence - LastAcknowledgedSequence)
        {
            throw new AgentStoreException("outbox_invariant_invalid");
        }
    }

    private async Task<EventStoreHealth> GetHealthCoreAsync(CancellationToken cancellationToken)
    {
        _ = RequireConnection();
        long pending = await CountPendingCoreAsync(cancellationToken);
        DateTimeOffset? oldestPendingAtUtc = null;
        if (pending > 0)
        {
            await using SqliteCommand oldestCommand = RequireConnection().CreateCommand();
            oldestCommand.CommandText = "SELECT MIN(OccurredAtUtc) FROM DurableEvents WHERE Sequence > $ack;";
            oldestCommand.Parameters.AddWithValue("$ack", LastAcknowledgedSequence);
            object? oldest = await oldestCommand.ExecuteScalarAsync(cancellationToken);
            if (oldest is string oldestText && DateTimeOffset.TryParse(
                    oldestText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed))
            {
                oldestPendingAtUtc = parsed;
            }
        }

        long storageBytes = GetFileSize(_databasePath) + GetFileSize($"{_databasePath}-wal");
        double ratio = Math.Max(
            (double)pending / _limits.MaximumPendingEvents,
            (double)storageBytes / _limits.MaximumStorageBytes);
        EventStoreCapacityState state = ratio switch
        {
            >= 1 => EventStoreCapacityState.Full,
            >= 0.9 => EventStoreCapacityState.Critical,
            >= 0.8 => EventStoreCapacityState.Warning,
            _ => EventStoreCapacityState.Healthy
        };
        return new EventStoreHealth(pending, storageBytes, state, oldestPendingAtUtc);
    }

    private async Task<long> CountPendingCoreAsync(CancellationToken cancellationToken)
    {
        await using SqliteCommand command = RequireConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DurableEvents WHERE Sequence > $ack;";
        command.Parameters.AddWithValue("$ack", LastAcknowledgedSequence);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private Task CheckpointCoreAsync(CancellationToken cancellationToken) =>
        ExecuteNonQueryAsync("PRAGMA wal_checkpoint(PASSIVE);", cancellationToken);

    private SqliteConnection RequireConnection() =>
        _connection ?? throw new InvalidOperationException("Event store has not been initialized.");

    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = RequireConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string?> ReadMetadataAsync(string key, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = RequireConnection().CreateCommand();
        command.CommandText = "SELECT MetadataValue FROM AgentMetadata WHERE MetadataKey = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task WriteMetadataAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = RequireConnection().CreateCommand();
        command.CommandText =
            """
            INSERT INTO AgentMetadata (MetadataKey, MetadataValue)
            VALUES ($key, $value)
            ON CONFLICT(MetadataKey) DO UPDATE SET MetadataValue = excluded.MetadataValue;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private StoredDurableEvent ReadStoredEvent(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            AgentInstanceId,
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            ParseTimestamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));

    private static Exception NormalizeStoreException(Exception exception)
    {
        if (exception is AgentStoreException)
        {
            return exception;
        }

        if (exception is SqliteException sqlite && sqlite.SqliteErrorCode is 11 or 26)
        {
            return new AgentStoreException("outbox_corrupt", exception);
        }

        return exception;
    }

    private static long GetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullableTimestamp(DateTimeOffset? value) =>
        value.HasValue ? FormatTimestamp(value.Value) : DBNull.Value;

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private const string CreateSchemaSql =
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
        CREATE INDEX IX_ProcessedCommands_ProcessedAtUtc ON ProcessedCommands (ProcessedAtUtc);
        """;

    private const string UpgradeV1ToV2Sql =
        """
        ALTER TABLE ProcessedCommands
            ADD COLUMN ExecutionState TEXT NOT NULL DEFAULT 'completed';
        UPDATE ProcessedCommands
        SET ExecutionState = 'executing'
        WHERE ResultJson LIKE '%command_outcome_unknown%';
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
        """;

    private const string UpgradeV2ToV3Sql =
        """
        ALTER TABLE DurableEvents ADD COLUMN CollectionId TEXT NULL;
        ALTER TABLE DurableEvents ADD COLUMN CollectionBindingId TEXT NULL;
        ALTER TABLE DurableEvents ADD COLUMN CallContextId TEXT NULL;
        ALTER TABLE ActiveCallJournal ADD COLUMN CollectionId TEXT NULL;
        ALTER TABLE ActiveCallJournal ADD COLUMN CollectionBindingId TEXT NULL;
        ALTER TABLE ActiveCallJournal ADD COLUMN CallContextId TEXT NULL;
        """;
}
