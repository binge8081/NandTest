using System.Globalization;
using Microsoft.Data.Sqlite;
using NandFlashTester.Core;
using NandFlashTester.Core.Materials;
using NandFlashTester.Core.Platform;

namespace NandFlashTester.Api.Data;

public sealed class StoredParameterImport
{
    public required string ImportId { get; init; }
    public required string Key { get; init; }
    public string? Version { get; init; }
    public string? DocumentId { get; init; }
    public string? Chapter { get; init; }
    public required string SourceStatus { get; init; }
    public bool Enabled { get; init; }
    public required string Reason { get; init; }
    public required string CreatedAt { get; init; }
}

public sealed class StoredAttempt
{
    public required string AttemptId { get; init; }
    public required string RecipeName { get; init; }
    public required string RecipeSha256 { get; init; }
    public required string RecipeSnapshot { get; init; }
    public long Seed { get; init; }
    public required string Status { get; init; }
    public required string Reason { get; init; }
    public required string NextStep { get; init; }
    public int BytesEmitted { get; init; }
    public bool HardwareAccepted { get; init; }
    public required string EvidenceClass { get; init; }
    public required string HostTimeout { get; init; }
    public required string CreatedAt { get; init; }
}

public sealed class SqliteTraceStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public SqliteTraceStore(string path) => _path = path;

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
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS parameter_imports (
                    import_id TEXT PRIMARY KEY,
                    param_key TEXT NOT NULL,
                    proposed_version TEXT,
                    document_id TEXT,
                    chapter TEXT,
                    source_status TEXT NOT NULL,
                    enabled INTEGER NOT NULL,
                    reason TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS run_attempts (
                    attempt_id TEXT PRIMARY KEY,
                    recipe_name TEXT NOT NULL,
                    recipe_sha256 TEXT NOT NULL,
                    recipe_snapshot TEXT NOT NULL,
                    seed INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    next_step TEXT NOT NULL,
                    bytes_emitted INTEGER NOT NULL,
                    hardware_accepted INTEGER NOT NULL,
                    evidence_class TEXT NOT NULL,
                    host_timeout TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }
    }

    public StoredParameterImport AddImport(ParameterImport import)
    {
        var stored = new StoredParameterImport
        {
            ImportId = "imp-" + Guid.NewGuid().ToString("N"),
            Key = import.Key,
            Version = import.Version,
            DocumentId = import.DocumentId,
            Chapter = import.Chapter,
            SourceStatus = import.SourceStatus,
            Enabled = import.Enabled,
            Reason = import.Reason,
            CreatedAt = Now()
        };
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO parameter_imports (
                    import_id, param_key, proposed_version, document_id, chapter, source_status, enabled, reason, created_at
                ) VALUES ($id, $key, $version, $doc, $chapter, $status, $enabled, $reason, $at)
                """;
            command.Parameters.AddWithValue("$id", stored.ImportId);
            command.Parameters.AddWithValue("$key", stored.Key);
            command.Parameters.AddWithValue("$version", (object?)stored.Version ?? DBNull.Value);
            command.Parameters.AddWithValue("$doc", (object?)stored.DocumentId ?? DBNull.Value);
            command.Parameters.AddWithValue("$chapter", (object?)stored.Chapter ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", stored.SourceStatus);
            command.Parameters.AddWithValue("$enabled", stored.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$reason", stored.Reason);
            command.Parameters.AddWithValue("$at", stored.CreatedAt);
            command.ExecuteNonQuery();
        }

        return stored;
    }

    public IReadOnlyList<StoredParameterImport> Imports()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT import_id, param_key, proposed_version, document_id, chapter, source_status, enabled, reason, created_at FROM parameter_imports ORDER BY created_at";
            var rows = new List<StoredParameterImport>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new StoredParameterImport
                {
                    ImportId = reader.GetString(0),
                    Key = reader.GetString(1),
                    Version = reader.IsDBNull(2) ? null : reader.GetString(2),
                    DocumentId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Chapter = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SourceStatus = reader.GetString(5),
                    Enabled = reader.GetInt64(6) != 0,
                    Reason = reader.GetString(7),
                    CreatedAt = reader.GetString(8)
                });
            }

            return rows;
        }
    }

    public StoredAttempt AddRefusal(string name, string sha, string snapshot, long seed)
    {
        var stored = new StoredAttempt
        {
            AttemptId = "run-" + Guid.NewGuid().ToString("N"),
            RecipeName = name,
            RecipeSha256 = sha,
            RecipeSnapshot = snapshot,
            Seed = seed,
            Status = "not_executed",
            Reason = "未连接设备，执行没有开始。",
            NextStep = "连接设备之前不要把这条记录当成实测。",
            BytesEmitted = 0,
            HardwareAccepted = false,
            EvidenceClass = EvidenceClasses.SoftwareContract,
            HostTimeout = "未开始",
            CreatedAt = Now()
        };
        Insert(stored);
        return stored;
    }

    public bool CancelBeforeStart(string attemptId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE run_attempts
                SET status = 'cancelled_before_start',
                    reason = '取消发生在执行之前。没有发送，没有主机计时。',
                    next_step = '状态保持未执行后的取消。没有实测指标。'
                WHERE attempt_id = $id AND status = 'not_executed'
                """;
            command.Parameters.AddWithValue("$id", attemptId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    public StoredAttempt? GetAttempt(string attemptId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM run_attempts WHERE attempt_id = $id";
            command.Parameters.AddWithValue("$id", attemptId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        }
    }

    public IReadOnlyList<StoredAttempt> Attempts()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM run_attempts ORDER BY created_at";
            var rows = new List<StoredAttempt>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(Map(reader));
            }

            return rows;
        }
    }

    public static string Csv(IReadOnlyList<StoredAttempt> attempts)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append('\uFEFF');
        builder.AppendLine("产品,记录编号,配置名称,配置哈希,种子,状态,已发送字节,实机通过,证据级别,主机计时,原因");
        foreach (var attempt in attempts)
        {
            builder.Append(ProductIdentity.Name).Append(',')
                .Append(attempt.AttemptId).Append(',')
                .Append(attempt.RecipeName).Append(',')
                .Append(attempt.RecipeSha256).Append(',')
                .Append(attempt.Seed.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(attempt.Status).Append(',')
                .Append(attempt.BytesEmitted.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(attempt.HardwareAccepted ? "是" : "否").Append(',')
                .Append(attempt.EvidenceClass).Append(',')
                .Append(attempt.HostTimeout).Append(',')
                .Append(attempt.Reason)
                .AppendLine();
        }

        return builder.ToString();
    }

    private void Insert(StoredAttempt stored)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO run_attempts (
                    attempt_id, recipe_name, recipe_sha256, recipe_snapshot, seed, status, reason, next_step,
                    bytes_emitted, hardware_accepted, evidence_class, host_timeout, created_at
                ) VALUES (
                    $id, $name, $sha, $snapshot, $seed, $status, $reason, $next,
                    0, 0, $evidence, $timeout, $at
                )
                """;
            command.Parameters.AddWithValue("$id", stored.AttemptId);
            command.Parameters.AddWithValue("$name", stored.RecipeName);
            command.Parameters.AddWithValue("$sha", stored.RecipeSha256);
            command.Parameters.AddWithValue("$snapshot", stored.RecipeSnapshot);
            command.Parameters.AddWithValue("$seed", stored.Seed);
            command.Parameters.AddWithValue("$status", stored.Status);
            command.Parameters.AddWithValue("$reason", stored.Reason);
            command.Parameters.AddWithValue("$next", stored.NextStep);
            command.Parameters.AddWithValue("$evidence", stored.EvidenceClass);
            command.Parameters.AddWithValue("$timeout", stored.HostTimeout);
            command.Parameters.AddWithValue("$at", stored.CreatedAt);
            command.ExecuteNonQuery();
        }
    }

    private static StoredAttempt Map(SqliteDataReader reader) => new()
    {
        AttemptId = reader.GetString(reader.GetOrdinal("attempt_id")),
        RecipeName = reader.GetString(reader.GetOrdinal("recipe_name")),
        RecipeSha256 = reader.GetString(reader.GetOrdinal("recipe_sha256")),
        RecipeSnapshot = reader.GetString(reader.GetOrdinal("recipe_snapshot")),
        Seed = reader.GetInt64(reader.GetOrdinal("seed")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        Reason = reader.GetString(reader.GetOrdinal("reason")),
        NextStep = reader.GetString(reader.GetOrdinal("next_step")),
        BytesEmitted = (int)reader.GetInt64(reader.GetOrdinal("bytes_emitted")),
        HardwareAccepted = reader.GetInt64(reader.GetOrdinal("hardware_accepted")) != 0,
        EvidenceClass = reader.GetString(reader.GetOrdinal("evidence_class")),
        HostTimeout = reader.GetString(reader.GetOrdinal("host_timeout")),
        CreatedAt = reader.GetString(reader.GetOrdinal("created_at"))
    };

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
