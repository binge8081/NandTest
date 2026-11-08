using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NandFlashTester.Core;

namespace NandFlashTester.Api.Data;

public sealed class StoredScript
{
    public required string ScriptId { get; init; }
    public required string Name { get; init; }
    public required string Body { get; init; }
    public required string Sha256 { get; init; }
    public required string Status { get; init; }
    public int EraseCount { get; init; }
    public int ProgramCount { get; init; }
    public int ReadCount { get; init; }
    public int CompareCount { get; init; }
    public bool HardwareExecuted { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}

public sealed class StoredScriptLog
{
    public long LogId { get; init; }
    public required string Kind { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public required string Message { get; init; }
    public bool HardwareExecuted { get; init; }
    public required string CreatedAt { get; init; }
}

public sealed class SqliteScriptStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public SqliteScriptStore(string path) => _path = path;

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
                CREATE TABLE IF NOT EXISTS script_documents (
                    script_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    body TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    status TEXT NOT NULL,
                    erase_count INTEGER NOT NULL,
                    program_count INTEGER NOT NULL,
                    read_count INTEGER NOT NULL,
                    compare_count INTEGER NOT NULL,
                    hardware_executed INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS script_logs (
                    log_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    script_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    line INTEGER,
                    source_column INTEGER,
                    message TEXT NOT NULL,
                    hardware_executed INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }
    }

    public StoredScript Create(string name, string body)
    {
        var now = Now();
        var stored = new StoredScript
        {
            ScriptId = "scr-" + Guid.NewGuid().ToString("N"),
            Name = name,
            Body = body,
            Sha256 = Hash(body),
            Status = "saved",
            EraseCount = 0,
            ProgramCount = 0,
            ReadCount = 0,
            CompareCount = 0,
            HardwareExecuted = false,
            CreatedAt = now,
            UpdatedAt = now
        };
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO script_documents (
                    script_id, name, body, sha256, status, erase_count, program_count, read_count, compare_count,
                    hardware_executed, created_at, updated_at
                ) VALUES (
                    $id, $name, $body, $sha, 'saved', 0, 0, 0, 0, 0, $at, $at
                )
                """;
            Bind(command, stored);
            command.ExecuteNonQuery();
        }

        AddLog(stored.ScriptId, "save", null, null, "已新建。硬件未执行。");
        return stored;
    }

    public StoredScript? Save(string scriptId, string name, string body)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE script_documents
                SET name = $name, body = $body, sha256 = $sha, status = 'saved',
                    erase_count = 0, program_count = 0, read_count = 0, compare_count = 0,
                    hardware_executed = 0, updated_at = $at
                WHERE script_id = $id
                """;
            command.Parameters.AddWithValue("$id", scriptId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$body", body);
            command.Parameters.AddWithValue("$sha", Hash(body));
            command.Parameters.AddWithValue("$at", Now());
            if (command.ExecuteNonQuery() != 1)
            {
                return null;
            }
        }

        AddLog(scriptId, "save", null, null, "已保存。先前的主机计划作废。硬件未执行。");
        return Get(scriptId);
    }

    public void StorePlan(string scriptId, string name, int erase, int program, int read, int compare)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE script_documents
                SET name = $name, status = 'planned', erase_count = $erase, program_count = $program,
                    read_count = $read, compare_count = $compare, hardware_executed = 0, updated_at = $at
                WHERE script_id = $id
                """;
            command.Parameters.AddWithValue("$id", scriptId);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$erase", erase);
            command.Parameters.AddWithValue("$program", program);
            command.Parameters.AddWithValue("$read", read);
            command.Parameters.AddWithValue("$compare", compare);
            command.Parameters.AddWithValue("$at", Now());
            command.ExecuteNonQuery();
        }
    }

    public bool Stop(string scriptId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE script_documents
                SET status = 'stopped_before_send', hardware_executed = 0, updated_at = $at
                WHERE script_id = $id
                """;
            command.Parameters.AddWithValue("$id", scriptId);
            command.Parameters.AddWithValue("$at", Now());
            if (command.ExecuteNonQuery() != 1)
            {
                return false;
            }
        }

        AddLog(scriptId, "stop", null, null, "停止发生在发送之前。没有硬件执行。");
        return true;
    }

    public void AddLog(string scriptId, string kind, int? line, int? column, string message)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO script_logs (script_id, kind, line, source_column, message, hardware_executed, created_at)
                VALUES ($id, $kind, $line, $column, $message, 0, $at)
                """;
            command.Parameters.AddWithValue("$id", scriptId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$line", (object?)line ?? DBNull.Value);
            command.Parameters.AddWithValue("$column", (object?)column ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$at", Now());
            command.ExecuteNonQuery();
        }
    }

    public StoredScript? Get(string scriptId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM script_documents WHERE script_id = $id";
            command.Parameters.AddWithValue("$id", scriptId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        }
    }

    public IReadOnlyList<StoredScript> List()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM script_documents ORDER BY updated_at";
            var rows = new List<StoredScript>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(Map(reader));
            }

            return rows;
        }
    }

    public IReadOnlyList<StoredScriptLog> Logs(string scriptId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT log_id, kind, line, source_column, message, hardware_executed, created_at FROM script_logs WHERE script_id = $id ORDER BY log_id";
            command.Parameters.AddWithValue("$id", scriptId);
            var rows = new List<StoredScriptLog>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new StoredScriptLog
                {
                    LogId = reader.GetInt64(0),
                    Kind = reader.GetString(1),
                    Line = reader.IsDBNull(2) ? null : (int)reader.GetInt64(2),
                    Column = reader.IsDBNull(3) ? null : (int)reader.GetInt64(3),
                    Message = reader.GetString(4),
                    HardwareExecuted = reader.GetInt64(5) != 0,
                    CreatedAt = reader.GetString(6)
                });
            }

            return rows;
        }
    }

    public static string Csv(StoredScript script, IReadOnlyList<StoredScriptLog> logs)
    {
        var builder = new StringBuilder();
        builder.Append('\uFEFF');
        builder.AppendLine("产品,脚本编号,配置名称,状态,擦除计数,写入计数,读回计数,比较计数,硬件已执行,说明");
        builder.AppendLine(string.Join(',', new[]
        {
            ProductIdentity.Name,
            script.ScriptId,
            script.Name,
            script.Status,
            script.EraseCount.ToString(CultureInfo.InvariantCulture),
            script.ProgramCount.ToString(CultureInfo.InvariantCulture),
            script.ReadCount.ToString(CultureInfo.InvariantCulture),
            script.CompareCount.ToString(CultureInfo.InvariantCulture),
            "否",
            "主机计划。硬件未执行。"
        }.Select(Cell)));
        builder.AppendLine("日志序号,种类,行,列,硬件已执行,内容");
        foreach (var log in logs)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                log.LogId.ToString(CultureInfo.InvariantCulture),
                log.Kind,
                log.Line?.ToString(CultureInfo.InvariantCulture) ?? "",
                log.Column?.ToString(CultureInfo.InvariantCulture) ?? "",
                "否",
                log.Message
            }.Select(Cell)));
        }

        return builder.ToString();
    }

    private static void Bind(SqliteCommand command, StoredScript stored)
    {
        command.Parameters.AddWithValue("$id", stored.ScriptId);
        command.Parameters.AddWithValue("$name", stored.Name);
        command.Parameters.AddWithValue("$body", stored.Body);
        command.Parameters.AddWithValue("$sha", stored.Sha256);
        command.Parameters.AddWithValue("$at", stored.CreatedAt);
    }

    private static StoredScript Map(SqliteDataReader reader) => new()
    {
        ScriptId = reader.GetString(reader.GetOrdinal("script_id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Body = reader.GetString(reader.GetOrdinal("body")),
        Sha256 = reader.GetString(reader.GetOrdinal("sha256")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        EraseCount = (int)reader.GetInt64(reader.GetOrdinal("erase_count")),
        ProgramCount = (int)reader.GetInt64(reader.GetOrdinal("program_count")),
        ReadCount = (int)reader.GetInt64(reader.GetOrdinal("read_count")),
        CompareCount = (int)reader.GetInt64(reader.GetOrdinal("compare_count")),
        HardwareExecuted = reader.GetInt64(reader.GetOrdinal("hardware_executed")) != 0,
        CreatedAt = reader.GetString(reader.GetOrdinal("created_at")),
        UpdatedAt = reader.GetString(reader.GetOrdinal("updated_at"))
    };

    private static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static string Cell(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
