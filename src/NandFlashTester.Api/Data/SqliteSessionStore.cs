using System.Globalization;
using Microsoft.Data.Sqlite;
using NandFlashTester.Core.Platform;

namespace NandFlashTester.Api.Data;

public sealed class StoredCommand
{
    public required string CommandId { get; init; }

    public int StepIndex { get; init; }

    public required string Intent { get; init; }

    public required string State { get; init; }

    public bool Sent { get; init; }

    public bool HardwareAccepted { get; init; }

    public required string EvidenceClass { get; init; }

    public required string Reason { get; init; }

    public required string NextStep { get; init; }
}

public sealed class StoredSession
{
    public required string SessionId { get; init; }

    public required string State { get; init; }

    public required string EvidenceClass { get; init; }

    public bool HardwareAccepted { get; init; }

    public int BytesEmitted { get; init; }

    public string? BlockReason { get; init; }

    public required string OpenedAt { get; init; }

    public string? FinishedAt { get; init; }

    public List<StoredCommand> Commands { get; } = [];
}

public sealed class SqliteSessionStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public SqliteSessionStore(string path)
    {
        _path = path;
    }

    public void Initialize()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        lock (_gate)
        {
            using var connection = Open();
            using var create = connection.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS device_sessions (
                    session_id TEXT PRIMARY KEY,
                    state TEXT NOT NULL,
                    evidence_class TEXT NOT NULL,
                    hardware_accepted INTEGER NOT NULL,
                    bytes_emitted INTEGER NOT NULL,
                    block_reason TEXT,
                    opened_at TEXT NOT NULL,
                    finished_at TEXT
                );
                CREATE TABLE IF NOT EXISTS session_commands (
                    command_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    step_index INTEGER NOT NULL,
                    intent TEXT NOT NULL,
                    state TEXT NOT NULL,
                    sent INTEGER NOT NULL,
                    hardware_accepted INTEGER NOT NULL,
                    evidence_class TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    next_step TEXT NOT NULL,
                    FOREIGN KEY (session_id) REFERENCES device_sessions(session_id)
                );
                """;
            create.ExecuteNonQuery();

            using var reconcile = connection.CreateCommand();
            reconcile.CommandText =
                """
                UPDATE device_sessions
                SET state = 'Interrupted',
                    finished_at = COALESCE(finished_at, $now),
                    block_reason = '进程重启。部分恢复语义未提供，未继续下发。'
                WHERE state IN ('Running', 'Cancelling', 'Ready', 'Opened')
                """;
            reconcile.Parameters.AddWithValue("$now", Now());
            reconcile.ExecuteNonQuery();
        }
    }

    public void InsertBlocked(string sessionId, string reason)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO device_sessions (
                    session_id, state, evidence_class, hardware_accepted, bytes_emitted, block_reason, opened_at
                ) VALUES ($id, 'Blocked', $evidence, 0, 0, $reason, $now)
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$evidence", EvidenceClasses.SoftwareContract);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        }
    }

    public void AddCommands(string sessionId, IReadOnlyList<CommandOutcome> outcomes)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            for (var i = 0; i < outcomes.Count; i++)
            {
                var outcome = outcomes[i];
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO session_commands (
                        command_id, session_id, step_index, intent, state, sent, hardware_accepted, evidence_class, reason, next_step
                    ) VALUES ($id, $session, $index, $intent, $state, $sent, $accepted, $evidence, $reason, $next)
                    """;
                command.Parameters.AddWithValue("$id", "cmd-" + Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$index", i);
                command.Parameters.AddWithValue("$intent", outcome.Intent);
                command.Parameters.AddWithValue("$state", outcome.State);
                command.Parameters.AddWithValue("$sent", outcome.Sent ? 1 : 0);
                command.Parameters.AddWithValue("$accepted", outcome.HardwareAccepted ? 1 : 0);
                command.Parameters.AddWithValue("$evidence", outcome.EvidenceClass);
                command.Parameters.AddWithValue("$reason", outcome.Reason);
                command.Parameters.AddWithValue("$next", outcome.NextStep);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void MarkCancelled(string sessionId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE device_sessions
                SET state = 'Cancelled', finished_at = $now
                WHERE session_id = $id AND state = 'Blocked'
                """;
            command.Parameters.AddWithValue("$now", Now());
            command.Parameters.AddWithValue("$id", sessionId);
            command.ExecuteNonQuery();
        }
    }

    public StoredSession? Get(string sessionId)
    {
        lock (_gate)
        {
            using var connection = Open();
            var session = ReadSession(connection, sessionId);
            if (session is null)
            {
                return null;
            }

            session.Commands.AddRange(ReadCommands(connection, sessionId));
            return session;
        }
    }

    public IReadOnlyList<StoredSession> List(int limit)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id FROM device_sessions ORDER BY opened_at DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            var ids = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    ids.Add(reader.GetString(0));
                }
            }

            return ids.Select(id =>
            {
                var session = ReadSession(connection, id)!;
                session.Commands.AddRange(ReadCommands(connection, id));
                return session;
            }).ToList();
        }
    }

    private static StoredSession? ReadSession(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM device_sessions WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new StoredSession
        {
            SessionId = reader.GetString(reader.GetOrdinal("session_id")),
            State = reader.GetString(reader.GetOrdinal("state")),
            EvidenceClass = reader.GetString(reader.GetOrdinal("evidence_class")),
            HardwareAccepted = reader.GetInt64(reader.GetOrdinal("hardware_accepted")) != 0,
            BytesEmitted = (int)reader.GetInt64(reader.GetOrdinal("bytes_emitted")),
            BlockReason = reader.IsDBNull(reader.GetOrdinal("block_reason")) ? null : reader.GetString(reader.GetOrdinal("block_reason")),
            OpenedAt = reader.GetString(reader.GetOrdinal("opened_at")),
            FinishedAt = reader.IsDBNull(reader.GetOrdinal("finished_at")) ? null : reader.GetString(reader.GetOrdinal("finished_at"))
        };
    }

    private static List<StoredCommand> ReadCommands(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT command_id, step_index, intent, state, sent, hardware_accepted, evidence_class, reason, next_step
            FROM session_commands WHERE session_id = $id ORDER BY step_index
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        var commands = new List<StoredCommand>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            commands.Add(new StoredCommand
            {
                CommandId = reader.GetString(0),
                StepIndex = (int)reader.GetInt64(1),
                Intent = reader.GetString(2),
                State = reader.GetString(3),
                Sent = reader.GetInt64(4) != 0,
                HardwareAccepted = reader.GetInt64(5) != 0,
                EvidenceClass = reader.GetString(6),
                Reason = reader.GetString(7),
                NextStep = reader.GetString(8)
            });
        }

        return commands;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static string Now() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
