using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NandFlashTester.Core.Patterns;

namespace NandFlashTester.Api.Data;

public sealed class SqlitePatternStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public SqlitePatternStore(string path) => _path = path;

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
                CREATE TABLE IF NOT EXISTS pattern_resources (
                    pattern_id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    version TEXT NOT NULL,
                    sha256 TEXT NOT NULL,
                    page_size INTEGER NOT NULL,
                    data BLOB NOT NULL,
                    created_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }
    }

    public PatternResource Save(PatternResource resource)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO pattern_resources (pattern_id, name, version, sha256, page_size, data, created_at)
                VALUES ($id, $name, $version, $sha, $page, $data, $at)
                ON CONFLICT(pattern_id) DO UPDATE SET
                    name = excluded.name,
                    version = excluded.version,
                    created_at = excluded.created_at
                """;
            command.Parameters.AddWithValue("$id", resource.Id);
            command.Parameters.AddWithValue("$name", resource.Name);
            command.Parameters.AddWithValue("$version", resource.Version);
            command.Parameters.AddWithValue("$sha", resource.Sha256);
            command.Parameters.AddWithValue("$page", resource.PageSize);
            command.Parameters.Add("$data", SqliteType.Blob).Value = resource.Data;
            command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        return resource;
    }

    public IReadOnlyDictionary<string, PatternResource> Catalog()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT pattern_id, name, version, sha256, page_size, data FROM pattern_resources";
            var map = new Dictionary<string, PatternResource>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var data = (byte[])reader["data"];
                var pageSize = (int)reader.GetInt64(reader.GetOrdinal("page_size"));
                map[reader.GetString(0)] = new PatternResource(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    pageSize,
                    data.Length / pageSize,
                    data);
            }

            return map;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_path};Mode=ReadWriteCreate;Cache=Shared");
        connection.Open();
        return connection;
    }
}
