using System.Globalization;
using Microsoft.Data.Sqlite;
using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Execution;
using NandFlashTester.Core.Recipes;

namespace NandFlashTester.Api.Data;

public sealed class StoredStep
{
    public int Index { get; init; }

    public required string Name { get; init; }

    public required string Status { get; init; }

    public string? Message { get; init; }

    public string? DetailJson { get; init; }

    public string? StartedAt { get; init; }

    public string? FinishedAt { get; init; }
}

public sealed class StoredError
{
    public int Page { get; init; }

    public int ByteOffset { get; init; }

    public int Bit { get; init; }

    public required string ExpectedHex { get; init; }

    public required string ActualHex { get; init; }
}

public sealed class StoredJob
{
    public required string JobId { get; init; }

    public required string RecipeName { get; init; }

    public required string RecipeVersion { get; init; }

    public required string RecipeSchema { get; init; }

    public required string RecipeSha256 { get; init; }

    public required string RecipeSnapshot { get; init; }

    public long Seed { get; init; }

    public required string RequestedDeviceId { get; init; }

    public required string DeviceId { get; init; }

    public required string DeviceKind { get; init; }

    public bool DeviceSubstituted { get; init; }

    public string? SubstitutionReason { get; init; }

    public required string DeviceSnapshot { get; init; }

    public required string Status { get; init; }

    public bool CancelRequested { get; init; }

    public int ErrorBitCount { get; init; }

    public int ErrorByteCount { get; init; }

    public string? FailureReason { get; init; }

    public required string ResultNature { get; init; }

    public required string CreatedAt { get; init; }

    public string? StartedAt { get; init; }

    public string? FinishedAt { get; init; }

    public List<StoredStep> Steps { get; init; } = [];

    public List<StoredError> Errors { get; init; } = [];
}

public sealed record JobInsert(
    string JobId,
    Recipe Recipe,
    string RequestedDeviceId,
    string DeviceId,
    bool Substituted,
    string? SubstitutionReason,
    string DeviceSnapshot);

public sealed class SqliteJobStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public SqliteJobStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public void Initialize()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        lock (_gate)
        {
            using var connection = Open();
            Execute(
                connection,
                """
                CREATE TABLE IF NOT EXISTS jobs (
                    job_id TEXT PRIMARY KEY,
                    recipe_name TEXT NOT NULL,
                    recipe_version TEXT NOT NULL,
                    recipe_schema TEXT NOT NULL,
                    recipe_sha256 TEXT NOT NULL,
                    recipe_snapshot TEXT NOT NULL,
                    seed INTEGER NOT NULL,
                    requested_device_id TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    device_kind TEXT NOT NULL,
                    device_substituted INTEGER NOT NULL,
                    substitution_reason TEXT,
                    device_snapshot TEXT NOT NULL,
                    status TEXT NOT NULL,
                    cancel_requested INTEGER NOT NULL DEFAULT 0,
                    error_bit_count INTEGER NOT NULL DEFAULT 0,
                    error_byte_count INTEGER NOT NULL DEFAULT 0,
                    failure_reason TEXT,
                    result_nature TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    started_at TEXT,
                    finished_at TEXT
                );

                CREATE TABLE IF NOT EXISTS job_steps (
                    job_id TEXT NOT NULL,
                    step_index INTEGER NOT NULL,
                    step_name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    message TEXT,
                    detail_json TEXT,
                    started_at TEXT,
                    finished_at TEXT,
                    PRIMARY KEY (job_id, step_index),
                    FOREIGN KEY (job_id) REFERENCES jobs(job_id)
                );

                CREATE TABLE IF NOT EXISTS job_errors (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    job_id TEXT NOT NULL,
                    page INTEGER NOT NULL,
                    byte_offset INTEGER NOT NULL,
                    bit_index INTEGER NOT NULL,
                    expected_hex TEXT NOT NULL,
                    actual_hex TEXT NOT NULL,
                    FOREIGN KEY (job_id) REFERENCES jobs(job_id)
                );

                CREATE INDEX IF NOT EXISTS idx_jobs_device_status ON jobs(device_id, status);
                CREATE INDEX IF NOT EXISTS idx_errors_job ON job_errors(job_id);
                """);

            using var reconcile = connection.CreateCommand();
            reconcile.CommandText =
                """
                UPDATE jobs
                SET status = $status,
                    failure_reason = $reason,
                    finished_at = COALESCE(finished_at, $now)
                WHERE status IN ('pending', 'running')
                """;
            reconcile.Parameters.AddWithValue("$status", JobStates.Interrupted);
            reconcile.Parameters.AddWithValue("$reason", "服务重启时任务尚未结束。首个闭环不恢复未完成任务，已保留现场供查询。");
            reconcile.Parameters.AddWithValue("$now", Timestamp());
            reconcile.ExecuteNonQuery();
        }
    }

    public string? FindActiveJob(string deviceId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT job_id FROM jobs
                WHERE device_id = $device AND status IN ('pending', 'running')
                ORDER BY created_at
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$device", deviceId);
            return command.ExecuteScalar() as string;
        }
    }

    public void Insert(JobInsert insert)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO jobs (
                        job_id, recipe_name, recipe_version, recipe_schema, recipe_sha256, recipe_snapshot,
                        seed, requested_device_id, device_id, device_kind, device_substituted, substitution_reason,
                        device_snapshot, status, cancel_requested, error_bit_count, error_byte_count,
                        result_nature, created_at
                    ) VALUES (
                        $jobId, $name, $version, $schema, $hash, $snapshot,
                        $seed, $requested, $device, $kind, $substituted, $reason,
                        $deviceSnapshot, $status, 0, 0, 0,
                        $nature, $created
                    )
                    """;
                command.Parameters.AddWithValue("$jobId", insert.JobId);
                command.Parameters.AddWithValue("$name", insert.Recipe.Name);
                command.Parameters.AddWithValue("$version", insert.Recipe.Version);
                command.Parameters.AddWithValue("$schema", RecipeContract.SchemaId);
                command.Parameters.AddWithValue("$hash", insert.Recipe.Sha256);
                command.Parameters.AddWithValue("$snapshot", insert.Recipe.CanonicalJson);
                command.Parameters.AddWithValue("$seed", (long)insert.Recipe.Seed);
                command.Parameters.AddWithValue("$requested", insert.RequestedDeviceId);
                command.Parameters.AddWithValue("$device", insert.DeviceId);
                command.Parameters.AddWithValue("$kind", SimIdentity.Kind);
                command.Parameters.AddWithValue("$substituted", insert.Substituted ? 1 : 0);
                command.Parameters.AddWithValue("$reason", (object?)insert.SubstitutionReason ?? DBNull.Value);
                command.Parameters.AddWithValue("$deviceSnapshot", insert.DeviceSnapshot);
                command.Parameters.AddWithValue("$status", JobStates.Pending);
                command.Parameters.AddWithValue("$nature", SimIdentity.ResultNature);
                command.Parameters.AddWithValue("$created", Timestamp());
                command.ExecuteNonQuery();
            }

            for (var i = 0; i < StepNames.All.Length; i++)
            {
                using var step = connection.CreateCommand();
                step.Transaction = transaction;
                step.CommandText =
                    """
                    INSERT INTO job_steps (job_id, step_index, step_name, status, message)
                    VALUES ($jobId, $index, $name, $status, $message)
                    """;
                step.Parameters.AddWithValue("$jobId", insert.JobId);
                step.Parameters.AddWithValue("$index", i);
                step.Parameters.AddWithValue("$name", StepNames.All[i]);
                step.Parameters.AddWithValue("$status", JobStates.Pending);
                step.Parameters.AddWithValue("$message", "等待执行");
                step.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public StoredJob? Get(string jobId)
    {
        lock (_gate)
        {
            using var connection = Open();
            var job = ReadJob(connection, jobId);
            if (job is null)
            {
                return null;
            }

            job.Steps.AddRange(ReadSteps(connection, jobId));
            job.Errors.AddRange(ReadErrors(connection, jobId));
            return job;
        }
    }

    public IReadOnlyList<StoredJob> List(int limit)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT job_id FROM jobs
                ORDER BY created_at DESC, job_id DESC
                LIMIT $limit
                """;
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
                var job = ReadJob(connection, id)!;
                job.Steps.AddRange(ReadSteps(connection, id));
                job.Errors.AddRange(ReadErrors(connection, id));
                return job;
            }).ToList();
        }
    }

    public bool IsCancelRequested(string jobId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT cancel_requested FROM jobs WHERE job_id = $id";
            command.Parameters.AddWithValue("$id", jobId);
            var value = command.ExecuteScalar();
            return value is long number && number != 0;
        }
    }

    public bool RequestCancel(string jobId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE jobs
                SET cancel_requested = 1
                WHERE job_id = $id AND status IN ('pending', 'running')
                """;
            command.Parameters.AddWithValue("$id", jobId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    public bool TryMarkRunning(string jobId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE jobs
                SET status = 'running', started_at = $now
                WHERE job_id = $id AND status = 'pending' AND cancel_requested = 0
                """;
            command.Parameters.AddWithValue("$id", jobId);
            command.Parameters.AddWithValue("$now", Timestamp());
            return command.ExecuteNonQuery() == 1;
        }
    }

    public void MarkStepStarted(string jobId, string step)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE job_steps
                SET status = 'running', started_at = $now
                WHERE job_id = $id AND step_name = $step
                """;
            command.Parameters.AddWithValue("$id", jobId);
            command.Parameters.AddWithValue("$step", step);
            command.Parameters.AddWithValue("$now", Timestamp());
            command.ExecuteNonQuery();
        }
    }

    public void MarkStepProgress(string jobId, string step, string message, string? detailJson)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE job_steps
                SET message = $message, detail_json = $detail
                WHERE job_id = $id AND step_name = $step AND status = 'running'
                """;
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$detail", (object?)detailJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", jobId);
            command.Parameters.AddWithValue("$step", step);
            command.ExecuteNonQuery();
        }
    }

    public void MarkStepFinished(string jobId, StepRecord record)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE job_steps
                SET status = $status,
                    message = $message,
                    detail_json = $detail,
                    finished_at = $now,
                    started_at = COALESCE(started_at, $now)
                WHERE job_id = $id AND step_name = $step
                """;
            command.Parameters.AddWithValue("$status", record.Status);
            command.Parameters.AddWithValue("$message", record.Message);
            command.Parameters.AddWithValue("$detail", (object?)record.DetailJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", Timestamp());
            command.Parameters.AddWithValue("$id", jobId);
            command.Parameters.AddWithValue("$step", record.Name);
            command.ExecuteNonQuery();
        }
    }

    public void SkipUnfinished(string jobId, string status, string message)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE job_steps
                SET status = $status, message = $message, finished_at = $now
                WHERE job_id = $id AND status IN ('pending', 'running')
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$now", Timestamp());
            command.Parameters.AddWithValue("$id", jobId);
            command.ExecuteNonQuery();
        }
    }

    public void Complete(string jobId, ClosedLoopOutcome outcome)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var current = ReadStatus(connection, transaction, jobId);
            if (current is null || JobStates.IsTerminal(current.Value.Status))
            {
                transaction.Commit();
                return;
            }

            var status = outcome.Status;
            var reason = outcome.Reason;
            if (current.Value.CancelRequested && status is JobStates.Succeeded or JobStates.Failed)
            {
                status = JobStates.Cancelled;
                reason = "取消已生效。状态保持已取消，不改写为成功或失败。";
            }

            if (outcome.Errors.Count > 0 && status != JobStates.Cancelled)
            {
                foreach (var error in outcome.Errors)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO job_errors (job_id, page, byte_offset, bit_index, expected_hex, actual_hex)
                        VALUES ($jobId, $page, $byteOffset, $bit, $expected, $actual)
                        """;
                    insert.Parameters.AddWithValue("$jobId", jobId);
                    insert.Parameters.AddWithValue("$page", error.Page);
                    insert.Parameters.AddWithValue("$byteOffset", error.ByteOffset);
                    insert.Parameters.AddWithValue("$bit", error.Bit);
                    insert.Parameters.AddWithValue("$expected", error.ExpectedHex);
                    insert.Parameters.AddWithValue("$actual", error.ActualHex);
                    insert.ExecuteNonQuery();
                }
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE jobs
                SET status = $status,
                    error_bit_count = $bits,
                    error_byte_count = $bytes,
                    failure_reason = $reason,
                    finished_at = $now
                WHERE job_id = $id AND status IN ('pending', 'running')
                """;
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$bits", status == JobStates.Cancelled ? 0 : outcome.ErrorBitCount);
            update.Parameters.AddWithValue("$bytes", status == JobStates.Cancelled ? 0 : outcome.ErrorByteCount);
            update.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", Timestamp());
            update.Parameters.AddWithValue("$id", jobId);
            update.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public void FailUnexpected(string jobId, string message)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE jobs
                SET status = 'failed', failure_reason = $reason, finished_at = $now
                WHERE job_id = $id AND status IN ('pending', 'running')
                """;
            command.Parameters.AddWithValue("$reason", message);
            command.Parameters.AddWithValue("$now", Timestamp());
            command.Parameters.AddWithValue("$id", jobId);
            var changed = command.ExecuteNonQuery();
            if (changed == 1)
            {
                using var steps = connection.CreateCommand();
                steps.CommandText =
                    """
                    UPDATE job_steps
                    SET status = 'skipped', message = $message, finished_at = $now
                    WHERE job_id = $id AND status IN ('pending', 'running')
                    """;
                steps.Parameters.AddWithValue("$message", "任务意外停止，未继续执行。");
                steps.Parameters.AddWithValue("$now", Timestamp());
                steps.Parameters.AddWithValue("$id", jobId);
                steps.ExecuteNonQuery();
            }
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static (string Status, bool CancelRequested)? ReadStatus(SqliteConnection connection, SqliteTransaction transaction, string jobId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status, cancel_requested FROM jobs WHERE job_id = $id";
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), reader.GetInt64(1) != 0);
    }

    private static StoredJob? ReadJob(SqliteConnection connection, string jobId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE job_id = $id";
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return MapJob(reader);
    }

    private static StoredJob MapJob(SqliteDataReader reader) => new()
    {
        JobId = reader.GetString(reader.GetOrdinal("job_id")),
        RecipeName = reader.GetString(reader.GetOrdinal("recipe_name")),
        RecipeVersion = reader.GetString(reader.GetOrdinal("recipe_version")),
        RecipeSchema = reader.GetString(reader.GetOrdinal("recipe_schema")),
        RecipeSha256 = reader.GetString(reader.GetOrdinal("recipe_sha256")),
        RecipeSnapshot = reader.GetString(reader.GetOrdinal("recipe_snapshot")),
        Seed = reader.GetInt64(reader.GetOrdinal("seed")),
        RequestedDeviceId = reader.GetString(reader.GetOrdinal("requested_device_id")),
        DeviceId = reader.GetString(reader.GetOrdinal("device_id")),
        DeviceKind = reader.GetString(reader.GetOrdinal("device_kind")),
        DeviceSubstituted = reader.GetInt64(reader.GetOrdinal("device_substituted")) != 0,
        SubstitutionReason = reader.IsDBNull(reader.GetOrdinal("substitution_reason")) ? null : reader.GetString(reader.GetOrdinal("substitution_reason")),
        DeviceSnapshot = reader.GetString(reader.GetOrdinal("device_snapshot")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        CancelRequested = reader.GetInt64(reader.GetOrdinal("cancel_requested")) != 0,
        ErrorBitCount = (int)reader.GetInt64(reader.GetOrdinal("error_bit_count")),
        ErrorByteCount = (int)reader.GetInt64(reader.GetOrdinal("error_byte_count")),
        FailureReason = reader.IsDBNull(reader.GetOrdinal("failure_reason")) ? null : reader.GetString(reader.GetOrdinal("failure_reason")),
        ResultNature = reader.GetString(reader.GetOrdinal("result_nature")),
        CreatedAt = reader.GetString(reader.GetOrdinal("created_at")),
        StartedAt = reader.IsDBNull(reader.GetOrdinal("started_at")) ? null : reader.GetString(reader.GetOrdinal("started_at")),
        FinishedAt = reader.IsDBNull(reader.GetOrdinal("finished_at")) ? null : reader.GetString(reader.GetOrdinal("finished_at"))
    };

    private static List<StoredStep> ReadSteps(SqliteConnection connection, string jobId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT step_index, step_name, status, message, detail_json, started_at, finished_at
            FROM job_steps
            WHERE job_id = $id
            ORDER BY step_index
            """;
        command.Parameters.AddWithValue("$id", jobId);
        var steps = new List<StoredStep>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            steps.Add(new StoredStep
            {
                Index = (int)reader.GetInt64(0),
                Name = reader.GetString(1),
                Status = reader.GetString(2),
                Message = reader.IsDBNull(3) ? null : reader.GetString(3),
                DetailJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                StartedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                FinishedAt = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }

        return steps;
    }

    private static List<StoredError> ReadErrors(SqliteConnection connection, string jobId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT page, byte_offset, bit_index, expected_hex, actual_hex
            FROM job_errors
            WHERE job_id = $id
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$id", jobId);
        var errors = new List<StoredError>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            errors.Add(new StoredError
            {
                Page = (int)reader.GetInt64(0),
                ByteOffset = (int)reader.GetInt64(1),
                Bit = (int)reader.GetInt64(2),
                ExpectedHex = reader.GetString(3),
                ActualHex = reader.GetString(4)
            });
        }

        return errors;
    }

    private static string Timestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
