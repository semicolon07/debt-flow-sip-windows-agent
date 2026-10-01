using System.Globalization;
using Microsoft.Data.Sqlite;
using DebtFlow.SipAgent.Application;
using DebtFlow.SipAgent.Protocol;

namespace DebtFlow.SipAgent.Persistence;

public sealed class SqliteAgentEventStore(string databasePath) : IAgentEventStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

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

            string? directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString());

            await connection.OpenAsync(cancellationToken);
            _connection = connection;

            await ExecuteNonQueryAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            await ExecuteNonQueryAsync("PRAGMA synchronous=FULL;", cancellationToken);
            await ExecuteNonQueryAsync("PRAGMA busy_timeout=5000;", cancellationToken);
            await ExecuteNonQueryAsync(
                """
                CREATE TABLE IF NOT EXISTS AgentMetadata (
                    MetadataKey TEXT NOT NULL PRIMARY KEY,
                    MetadataValue TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS DurableEvents (
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

                CREATE TABLE IF NOT EXISTS ProcessedCommands (
                    CommandId TEXT NOT NULL PRIMARY KEY,
                    CommandType TEXT NOT NULL,
                    RequestHash TEXT NOT NULL,
                    ResultJson TEXT NOT NULL,
                    ProcessedAtUtc TEXT NOT NULL
                );

                PRAGMA user_version=1;
                """,
                cancellationToken);

            AgentInstanceId = await ReadMetadataAsync("agent_instance_id", cancellationToken)
                ?? ProtocolCodec.NewId();
            await WriteMetadataAsync("agent_instance_id", AgentInstanceId, cancellationToken);

            string? acknowledged = await ReadMetadataAsync("last_acknowledged_sequence", cancellationToken);
            LastAcknowledgedSequence = long.TryParse(acknowledged, NumberStyles.None, CultureInfo.InvariantCulture, out long ack)
                ? ack
                : 0;

            await using SqliteCommand sequenceCommand = connection.CreateCommand();
            sequenceCommand.CommandText = "SELECT COALESCE(MAX(Sequence), $ack) FROM DurableEvents;";
            sequenceCommand.Parameters.AddWithValue("$ack", LastAcknowledgedSequence);
            object? scalar = await sequenceCommand.ExecuteScalarAsync(cancellationToken);
            LastSequence = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
        catch
        {
            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredDurableEvent> AppendAsync(DurableEventDraft draft, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SqliteConnection connection = RequireConnection();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO DurableEvents
                    (EventId, AgentSessionId, CallId, CommandId, EventType, OccurredAtUtc, CallState, DataJson)
                VALUES
                    ($eventId, $agentSessionId, $callId, $commandId, $eventType, $occurredAtUtc, $callState, $dataJson);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$eventId", draft.EventId);
            command.Parameters.AddWithValue("$agentSessionId", draft.AgentSessionId);
            command.Parameters.AddWithValue("$callId", draft.CallId);
            command.Parameters.AddWithValue("$commandId", (object?)draft.CommandId ?? DBNull.Value);
            command.Parameters.AddWithValue("$eventType", draft.EventType);
            command.Parameters.AddWithValue("$occurredAtUtc", draft.OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$callState", (object?)draft.State ?? DBNull.Value);
            command.Parameters.AddWithValue("$dataJson", draft.DataJson);

            object? scalar = await command.ExecuteScalarAsync(cancellationToken);
            long sequence = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
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
                draft.DataJson);
        }
        finally
        {
            _gate.Release();
        }
    }

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
            SqliteConnection connection = RequireConnection();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT Sequence, EventId, AgentSessionId, CallId, CommandId, EventType,
                       OccurredAtUtc, CallState, DataJson
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
            await using SqliteCommand command = RequireConnection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM DurableEvents WHERE Sequence > $ack;";
            command.Parameters.AddWithValue("$ack", LastAcknowledgedSequence);
            object? scalar = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
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
            long actual = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
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
                SELECT CommandId, CommandType, RequestHash, ResultJson, ProcessedAtUtc
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
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveCommandAsync(ProcessedCommand command, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using SqliteCommand sqliteCommand = RequireConnection().CreateCommand();
            sqliteCommand.CommandText =
                """
                INSERT INTO ProcessedCommands
                    (CommandId, CommandType, RequestHash, ResultJson, ProcessedAtUtc)
                VALUES
                    ($commandId, $commandType, $requestHash, $resultJson, $processedAtUtc)
                ON CONFLICT(CommandId) DO UPDATE SET
                    CommandType = excluded.CommandType,
                    RequestHash = excluded.RequestHash,
                    ResultJson = excluded.ResultJson,
                    ProcessedAtUtc = excluded.ProcessedAtUtc;
                """;
            sqliteCommand.Parameters.AddWithValue("$commandId", command.CommandId);
            sqliteCommand.Parameters.AddWithValue("$commandType", command.CommandType);
            sqliteCommand.Parameters.AddWithValue("$requestHash", command.RequestHash);
            sqliteCommand.Parameters.AddWithValue("$resultJson", command.ResultJson);
            sqliteCommand.Parameters.AddWithValue("$processedAtUtc", command.ProcessedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await sqliteCommand.ExecuteNonQueryAsync(cancellationToken);
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
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8));
}
