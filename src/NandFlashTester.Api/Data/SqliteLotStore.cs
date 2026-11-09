using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NandFlashTester.Core.Lots;

namespace NandFlashTester.Api.Data;

public sealed class StoredSkuConfig
{
    public required string ConfigId { get; init; }
    public int Version { get; init; }
    public required string Name { get; init; }
    public int RetestLimit { get; init; }
    public required string TemperatureCandidate { get; init; }
    public bool RawEnabled { get; init; }
    public int RawQuotaBytes { get; init; }
    public required string Sha256 { get; init; }
    public required string Status { get; init; }
    public required string CreatedAt { get; init; }
}

public sealed class StoredLot
{
    public required string LotId { get; init; }
    public required string ConfigId { get; init; }
    public int ConfigVersion { get; init; }
    public required string ConfigSha256 { get; init; }
    public int IndustrialPlanned { get; init; }
    public int TotalPlanned { get; init; }
    public required string Status { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}

public sealed class StoredLotAttempt
{
    public required string AttemptId { get; init; }
    public required string LotId { get; init; }
    public required string Label { get; init; }
    public int AttemptIndex { get; init; }
    public string? ParentAttemptId { get; init; }
    public required string Status { get; init; }
    public bool SocketChangeRequired { get; init; }
    public bool Measured { get; init; }
    public int BytesEmitted { get; init; }
    public string? RawSha256 { get; init; }
    public int RawLength { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}

public sealed class SqliteLotStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public SqliteLotStore(string path) => _path = path;

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
                CREATE TABLE IF NOT EXISTS sku_configs (
                    config_id TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    retest_limit INTEGER NOT NULL,
                    temperature_candidate TEXT NOT NULL,
                    raw_enabled INTEGER NOT NULL,
                    raw_quota_bytes INTEGER NOT NULL,
                    sha256 TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (config_id, version)
                );
                CREATE TABLE IF NOT EXISTS lots (
                    lot_id TEXT PRIMARY KEY,
                    config_id TEXT NOT NULL,
                    config_version INTEGER NOT NULL,
                    config_sha256 TEXT NOT NULL,
                    industrial_planned INTEGER NOT NULL,
                    total_planned INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS lot_attempts (
                    attempt_id TEXT PRIMARY KEY,
                    lot_id TEXT NOT NULL,
                    label TEXT NOT NULL,
                    attempt_index INTEGER NOT NULL,
                    parent_attempt_id TEXT,
                    status TEXT NOT NULL,
                    socket_change_required INTEGER NOT NULL,
                    measured INTEGER NOT NULL,
                    bytes_emitted INTEGER NOT NULL,
                    executed_rate_mt_per_s INTEGER,
                    ecc_definition TEXT,
                    chamber_samples INTEGER,
                    raw_sha256 TEXT,
                    raw_length INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE (lot_id, label, attempt_index)
                );
                CREATE TABLE IF NOT EXISTS lot_raw (
                    attempt_id TEXT PRIMARY KEY,
                    data BLOB NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            using var reconcile = connection.CreateCommand();
            reconcile.CommandText =
                """
                UPDATE lot_attempts
                SET status = 'interrupted_unrecorded',
                    bytes_emitted = 0,
                    measured = 0,
                    updated_at = $now
                WHERE status = 'recording'
                """;
            reconcile.Parameters.AddWithValue("$now", Now());
            reconcile.ExecuteNonQuery();
        }
    }

    public StoredSkuConfig CreateConfig(string name, int retestLimit, string? temperatureCandidate, bool rawEnabled, int rawQuotaBytes)
    {
        var (candidate, enabled, quota) = LotRules.ValidateConfig(name, retestLimit, temperatureCandidate, rawEnabled, rawQuotaBytes);
        var now = Now();
        var stored = new StoredSkuConfig
        {
            ConfigId = "cfg-" + Guid.NewGuid().ToString("N"),
            Version = 1,
            Name = name,
            RetestLimit = retestLimit,
            TemperatureCandidate = candidate,
            RawEnabled = enabled,
            RawQuotaBytes = quota,
            Sha256 = LotRules.Hash(LotRules.Canonical(name, retestLimit, candidate, enabled, quota)),
            Status = "draft",
            CreatedAt = now
        };
        InsertConfig(stored);
        return stored;
    }

    public StoredSkuConfig Lock(string configId, int version)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var stored = ReadConfig(connection, transaction, configId, version) ?? throw new LotRuleException("配置版本不存在。");
            if (stored.Status != "draft")
            {
                throw new LotRuleException("只有草稿可以锁定。已锁定的版本保持不变。");
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE sku_configs SET status = 'locked' WHERE config_id = $id AND version = $version AND status = 'draft'";
            command.Parameters.AddWithValue("$id", configId);
            command.Parameters.AddWithValue("$version", version);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new LotRuleException("配置没有锁定。");
            }

            transaction.Commit();
            return ReadConfig(connection, null, configId, version)!;
        }
    }

    public StoredSkuConfig Revise(string configId, string name, int retestLimit, string? temperatureCandidate, bool rawEnabled, int rawQuotaBytes)
    {
        var (candidate, enabled, quota) = LotRules.ValidateConfig(name, retestLimit, temperatureCandidate, rawEnabled, rawQuotaBytes);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var versions = ReadConfigs(connection, transaction, configId);
            if (versions.Count == 0)
            {
                throw new LotRuleException("配置不存在。");
            }

            var latest = versions[^1];
            if (latest.Status != "locked")
            {
                throw new LotRuleException("当前版本还是草稿。先锁定，或直接使用这份草稿，不另开版本。");
            }

            var stored = new StoredSkuConfig
            {
                ConfigId = configId,
                Version = latest.Version + 1,
                Name = name,
                RetestLimit = retestLimit,
                TemperatureCandidate = candidate,
                RawEnabled = enabled,
                RawQuotaBytes = quota,
                Sha256 = LotRules.Hash(LotRules.Canonical(name, retestLimit, candidate, enabled, quota)),
                Status = "draft",
                CreatedAt = Now()
            };
            InsertConfig(connection, transaction, stored);
            transaction.Commit();
            return stored;
        }
    }

    public IReadOnlyList<StoredSkuConfig> Configs(string configId)
    {
        lock (_gate)
        {
            using var connection = Open();
            return ReadConfigs(connection, null, configId);
        }
    }

    public StoredLot OpenLot(string configId, int version, int industrialPlanned, int totalPlanned)
    {
        LotRules.ValidateShare(industrialPlanned, totalPlanned);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var config = ReadConfig(connection, transaction, configId, version) ?? throw new LotRuleException("配置版本不存在。");
            if (config.Status != "locked")
            {
                throw new LotRuleException("配置尚未锁定，不能开批次。");
            }

            var now = Now();
            var lot = new StoredLot
            {
                LotId = "lot-" + Guid.NewGuid().ToString("N"),
                ConfigId = config.ConfigId,
                ConfigVersion = config.Version,
                ConfigSha256 = config.Sha256,
                IndustrialPlanned = industrialPlanned,
                TotalPlanned = totalPlanned,
                Status = "open",
                CreatedAt = now,
                UpdatedAt = now
            };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO lots (
                    lot_id, config_id, config_version, config_sha256, industrial_planned, total_planned, status, created_at, updated_at
                ) VALUES ($id, $cfg, $ver, $sha, $ind, $total, 'open', $at, $at)
                """;
            command.Parameters.AddWithValue("$id", lot.LotId);
            command.Parameters.AddWithValue("$cfg", lot.ConfigId);
            command.Parameters.AddWithValue("$ver", lot.ConfigVersion);
            command.Parameters.AddWithValue("$sha", lot.ConfigSha256);
            command.Parameters.AddWithValue("$ind", lot.IndustrialPlanned);
            command.Parameters.AddWithValue("$total", lot.TotalPlanned);
            command.Parameters.AddWithValue("$at", now);
            command.ExecuteNonQuery();
            transaction.Commit();
            return lot;
        }
    }

    public StoredLot? Lot(string lotId)
    {
        lock (_gate)
        {
            using var connection = Open();
            return ReadLot(connection, null, lotId);
        }
    }

    public StoredLot Close(string lotId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var lot = ReadLot(connection, transaction, lotId) ?? throw new LotRuleException("批次不存在。");
            if (lot.Status != "open")
            {
                throw new LotRuleException("批次已经关闭。");
            }

            var now = Now();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE lots SET status = 'closed', updated_at = $now WHERE lot_id = $id AND status = 'open'";
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$id", lotId);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new LotRuleException("批次没有关闭。");
            }

            transaction.Commit();
            return ReadLot(connection, null, lotId)!;
        }
    }

    public StoredLotAttempt Record(string lotId, string label) => AddAttempt(lotId, label, retest: false);

    public StoredLotAttempt Retest(string lotId, string label) => AddAttempt(lotId, label, retest: true);

    public void LeaveRecording(string lotId, string label)
    {
        LotRules.ValidateName(label);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO lot_attempts (
                    attempt_id, lot_id, label, attempt_index, parent_attempt_id, status, socket_change_required,
                    measured, bytes_emitted, executed_rate_mt_per_s, ecc_definition, chamber_samples, raw_sha256, raw_length, created_at, updated_at
                ) VALUES ($id, $lot, $label, 1, NULL, 'recording', 0, 0, 0, NULL, NULL, NULL, NULL, 0, $at, $at)
                """;
            var now = Now();
            command.Parameters.AddWithValue("$id", "att-crash");
            command.Parameters.AddWithValue("$lot", lotId);
            command.Parameters.AddWithValue("$label", label);
            command.Parameters.AddWithValue("$at", now);
            command.ExecuteNonQuery();
        }
    }

    public int RawReadLimit(string lotId, string label, int attemptIndex)
    {
        lock (_gate)
        {
            using var connection = Open();
            var lot = ReadLot(connection, null, lotId) ?? throw new LotRuleException("批次不存在。");
            var config = ReadConfig(connection, null, lot.ConfigId, lot.ConfigVersion) ?? throw new LotRuleException("批次引用的配置不存在。");
            if (!config.RawEnabled)
            {
                throw new LotRuleException("raw 默认关闭。本批次配置没有打开主机 raw，本次不读取。");
            }

            var attempt = ReadAttempt(connection, null, lotId, label, attemptIndex) ?? throw new LotRuleException("记录不存在。");
            if (attempt.Status != "recorded")
            {
                throw new LotRuleException("只有已提交的记录可以保存 raw。");
            }

            if (attempt.RawSha256 is not null)
            {
                throw new LotRuleException("该次记录已经有 raw。");
            }

            return config.RawQuotaBytes;
        }
    }

    public StoredLotAttempt AttachRaw(string lotId, string label, int attemptIndex, byte[] data)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var lot = ReadLot(connection, transaction, lotId) ?? throw new LotRuleException("批次不存在。");
            var config = ReadConfig(connection, transaction, lot.ConfigId, lot.ConfigVersion) ?? throw new LotRuleException("批次引用的配置不存在。");
            LotRules.ValidateRaw(config.RawEnabled, config.RawQuotaBytes, data.Length);
            var attempt = ReadAttempt(connection, transaction, lotId, label, attemptIndex) ?? throw new LotRuleException("记录不存在。");
            if (attempt.Status != "recorded")
            {
                throw new LotRuleException("只有已提交的记录可以保存 raw。");
            }

            if (attempt.RawSha256 is not null)
            {
                throw new LotRuleException("该次记录已经有 raw。");
            }

            var sha = Convert.ToHexString(SHA256.HashData(data));
            using (var blob = connection.CreateCommand())
            {
                blob.Transaction = transaction;
                blob.CommandText = "INSERT INTO lot_raw (attempt_id, data) VALUES ($id, $data)";
                blob.Parameters.AddWithValue("$id", attempt.AttemptId);
                blob.Parameters.AddWithValue("$data", data);
                blob.ExecuteNonQuery();
            }

            var now = Now();
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE lot_attempts SET raw_sha256 = $sha, raw_length = $len, updated_at = $now WHERE attempt_id = $id";
                update.Parameters.AddWithValue("$sha", sha);
                update.Parameters.AddWithValue("$len", data.Length);
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$id", attempt.AttemptId);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
            return ReadAttempt(connection, null, lotId, label, attemptIndex)!;
        }
    }

    public IReadOnlyList<StoredLotAttempt> Attempts(string lotId)
    {
        lock (_gate)
        {
            using var connection = Open();
            return ReadAttempts(connection, null, lotId);
        }
    }

    private StoredLotAttempt AddAttempt(string lotId, string label, bool retest)
    {
        LotRules.ValidateName(label);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var lot = ReadLot(connection, transaction, lotId) ?? throw new LotRuleException("批次不存在。");
            if (lot.Status != "open")
            {
                throw new LotRuleException("批次已关闭，不能再记录或复测。");
            }

            var config = ReadConfig(connection, transaction, lot.ConfigId, lot.ConfigVersion) ?? throw new LotRuleException("批次引用的配置不存在。");
            var recorded = ReadAttempts(connection, transaction, lotId).Where(item => item.Label == label && item.Status == "recorded").ToArray();
            if (!retest && recorded.Length > 0)
            {
                throw new LotRuleException("该片已有记录。下一次应走复测。");
            }

            if (retest && recorded.Length == 0)
            {
                throw new LotRuleException("还没有首次记录，不能复测。");
            }

            if (retest && recorded.Length >= config.RetestLimit + 1)
            {
                throw new LotRuleException($"复测次数已达配置上限 {config.RetestLimit}。不强制换座，也不产生分档。");
            }

            var now = Now();
            var attempt = new StoredLotAttempt
            {
                AttemptId = "att-" + Guid.NewGuid().ToString("N"),
                LotId = lotId,
                Label = label,
                AttemptIndex = recorded.Length + 1,
                ParentAttemptId = retest ? recorded[^1].AttemptId : null,
                Status = "recorded",
                SocketChangeRequired = false,
                Measured = false,
                BytesEmitted = 0,
                RawSha256 = null,
                RawLength = 0,
                CreatedAt = now,
                UpdatedAt = now
            };
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO lot_attempts (
                        attempt_id, lot_id, label, attempt_index, parent_attempt_id, status, socket_change_required,
                        measured, bytes_emitted, executed_rate_mt_per_s, ecc_definition, chamber_samples, raw_sha256, raw_length, created_at, updated_at
                    ) VALUES (
                        $id, $lot, $label, $index, $parent, 'recording', 0,
                        0, 0, NULL, NULL, NULL, NULL, 0, $at, $at
                    )
                    """;
                insert.Parameters.AddWithValue("$id", attempt.AttemptId);
                insert.Parameters.AddWithValue("$lot", lotId);
                insert.Parameters.AddWithValue("$label", label);
                insert.Parameters.AddWithValue("$index", attempt.AttemptIndex);
                insert.Parameters.AddWithValue("$parent", (object?)attempt.ParentAttemptId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$at", now);
                insert.ExecuteNonQuery();
            }

            using (var finish = connection.CreateCommand())
            {
                finish.Transaction = transaction;
                finish.CommandText = "UPDATE lot_attempts SET status = 'recorded', updated_at = $at WHERE attempt_id = $id AND status = 'recording'";
                finish.Parameters.AddWithValue("$at", now);
                finish.Parameters.AddWithValue("$id", attempt.AttemptId);
                if (finish.ExecuteNonQuery() != 1)
                {
                    throw new LotRuleException("记录没有提交。");
                }
            }

            transaction.Commit();
            return attempt;
        }
    }

    private void InsertConfig(StoredSkuConfig stored)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            InsertConfig(connection, transaction, stored);
            transaction.Commit();
        }
    }

    private static void InsertConfig(SqliteConnection connection, SqliteTransaction transaction, StoredSkuConfig stored)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO sku_configs (
                config_id, version, name, retest_limit, temperature_candidate, raw_enabled, raw_quota_bytes, sha256, status, created_at
            ) VALUES ($id, $version, $name, $limit, $candidate, $raw, $quota, $sha, $status, $at)
            """;
        command.Parameters.AddWithValue("$id", stored.ConfigId);
        command.Parameters.AddWithValue("$version", stored.Version);
        command.Parameters.AddWithValue("$name", stored.Name);
        command.Parameters.AddWithValue("$limit", stored.RetestLimit);
        command.Parameters.AddWithValue("$candidate", stored.TemperatureCandidate);
        command.Parameters.AddWithValue("$raw", stored.RawEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$quota", stored.RawQuotaBytes);
        command.Parameters.AddWithValue("$sha", stored.Sha256);
        command.Parameters.AddWithValue("$status", stored.Status);
        command.Parameters.AddWithValue("$at", stored.CreatedAt);
        command.ExecuteNonQuery();
    }

    private static StoredSkuConfig? ReadConfig(SqliteConnection connection, SqliteTransaction? transaction, string configId, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM sku_configs WHERE config_id = $id AND version = $version";
        command.Parameters.AddWithValue("$id", configId);
        command.Parameters.AddWithValue("$version", version);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapConfig(reader) : null;
    }

    private static List<StoredSkuConfig> ReadConfigs(SqliteConnection connection, SqliteTransaction? transaction, string configId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM sku_configs WHERE config_id = $id ORDER BY version";
        command.Parameters.AddWithValue("$id", configId);
        var rows = new List<StoredSkuConfig>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(MapConfig(reader));
        }

        return rows;
    }

    private static StoredLot? ReadLot(SqliteConnection connection, SqliteTransaction? transaction, string lotId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM lots WHERE lot_id = $id";
        command.Parameters.AddWithValue("$id", lotId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapLot(reader) : null;
    }

    private static StoredLotAttempt? ReadAttempt(SqliteConnection connection, SqliteTransaction? transaction, string lotId, string label, int attemptIndex)
    {
        return ReadAttempts(connection, transaction, lotId).SingleOrDefault(item => item.Label == label && item.AttemptIndex == attemptIndex);
    }

    private static List<StoredLotAttempt> ReadAttempts(SqliteConnection connection, SqliteTransaction? transaction, string lotId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM lot_attempts WHERE lot_id = $id ORDER BY label, attempt_index";
        command.Parameters.AddWithValue("$id", lotId);
        var rows = new List<StoredLotAttempt>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(MapAttempt(reader));
        }

        return rows;
    }

    private static StoredSkuConfig MapConfig(SqliteDataReader reader) => new()
    {
        ConfigId = reader.GetString(reader.GetOrdinal("config_id")),
        Version = (int)reader.GetInt64(reader.GetOrdinal("version")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        RetestLimit = (int)reader.GetInt64(reader.GetOrdinal("retest_limit")),
        TemperatureCandidate = reader.GetString(reader.GetOrdinal("temperature_candidate")),
        RawEnabled = reader.GetInt64(reader.GetOrdinal("raw_enabled")) != 0,
        RawQuotaBytes = (int)reader.GetInt64(reader.GetOrdinal("raw_quota_bytes")),
        Sha256 = reader.GetString(reader.GetOrdinal("sha256")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        CreatedAt = reader.GetString(reader.GetOrdinal("created_at"))
    };

    private static StoredLot MapLot(SqliteDataReader reader) => new()
    {
        LotId = reader.GetString(reader.GetOrdinal("lot_id")),
        ConfigId = reader.GetString(reader.GetOrdinal("config_id")),
        ConfigVersion = (int)reader.GetInt64(reader.GetOrdinal("config_version")),
        ConfigSha256 = reader.GetString(reader.GetOrdinal("config_sha256")),
        IndustrialPlanned = (int)reader.GetInt64(reader.GetOrdinal("industrial_planned")),
        TotalPlanned = (int)reader.GetInt64(reader.GetOrdinal("total_planned")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        CreatedAt = reader.GetString(reader.GetOrdinal("created_at")),
        UpdatedAt = reader.GetString(reader.GetOrdinal("updated_at"))
    };

    private static StoredLotAttempt MapAttempt(SqliteDataReader reader) => new()
    {
        AttemptId = reader.GetString(reader.GetOrdinal("attempt_id")),
        LotId = reader.GetString(reader.GetOrdinal("lot_id")),
        Label = reader.GetString(reader.GetOrdinal("label")),
        AttemptIndex = (int)reader.GetInt64(reader.GetOrdinal("attempt_index")),
        ParentAttemptId = reader.IsDBNull(reader.GetOrdinal("parent_attempt_id")) ? null : reader.GetString(reader.GetOrdinal("parent_attempt_id")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        SocketChangeRequired = reader.GetInt64(reader.GetOrdinal("socket_change_required")) != 0,
        Measured = reader.GetInt64(reader.GetOrdinal("measured")) != 0,
        BytesEmitted = (int)reader.GetInt64(reader.GetOrdinal("bytes_emitted")),
        RawSha256 = reader.IsDBNull(reader.GetOrdinal("raw_sha256")) ? null : reader.GetString(reader.GetOrdinal("raw_sha256")),
        RawLength = (int)reader.GetInt64(reader.GetOrdinal("raw_length")),
        CreatedAt = reader.GetString(reader.GetOrdinal("created_at")),
        UpdatedAt = reader.GetString(reader.GetOrdinal("updated_at"))
    };

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
