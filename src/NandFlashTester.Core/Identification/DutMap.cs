using System.Text.Json;
using System.Text.Json.Serialization;

namespace NandFlashTester.Core.Identification;

public sealed record DutTarget(int Lane, int Channel, int Ce, int Lun)
{
    public string Id => $"{Lane}.{Channel}.{Ce}.{Lun}";

    public string Label => $"Lane {Lane} · 通道 {Channel} · CE {Ce} · LUN {Lun}";
}

public sealed class DutMapLoad
{
    public string Path { get; init; } = "";
    public string Source { get; init; } = "";
    public IReadOnlyList<DutTarget> Targets { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public static class DutMapLoader
{
    public const int MaxTargets = 8192;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    public static string? ResolvePath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var env = Environment.GetEnvironmentVariable("NFA1000_DUT_MAP");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    public static DutMapLoad Load(string? configured)
    {
        var path = ResolvePath(configured);
        if (path is null)
        {
            return new DutMapLoad
            {
                Source = "未载入",
                Errors = ["没有 DUT_MAP 文件。寄存器读取未冻结，软件不从设备猜测 Lane、通道、CE 或 LUN。"]
            };
        }

        if (!File.Exists(path))
        {
            return new DutMapLoad { Path = path, Source = "文件", Errors = [$"DUT_MAP 文件不存在：{path}"] };
        }

        try
        {
            var document = JsonSerializer.Deserialize<DutMapDocument>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("DUT_MAP 是空的。");
            if (document.SchemaVersion != "1")
            {
                throw new InvalidDataException("DUT_MAP 的 schemaVersion 必须是 1。");
            }

            var targets = new List<DutTarget>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.Targets ?? [])
            {
                if (item.Lane < 0 || item.Channel < 0 || item.Ce < 0 || item.Lun < 0)
                {
                    throw new InvalidDataException("Lane、通道、CE、LUN 不能是负数。");
                }

                var target = new DutTarget(item.Lane, item.Channel, item.Ce, item.Lun);
                if (!seen.Add(target.Id))
                {
                    throw new InvalidDataException($"DUT_MAP 里有重复目标 {target.Id}。");
                }

                targets.Add(target);
                if (targets.Count > MaxTargets)
                {
                    throw new InvalidDataException($"DUT_MAP 超过 {MaxTargets} 条。");
                }
            }

            return new DutMapLoad { Path = path, Source = "用户文件", Targets = targets };
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return new DutMapLoad { Path = path, Source = "文件", Errors = [ex.Message] };
        }
    }

    private sealed class DutMapDocument
    {
        public required string SchemaVersion { get; init; }
        public string? Source { get; init; }
        public List<TargetDocument>? Targets { get; init; }
    }

    private sealed class TargetDocument
    {
        public int Lane { get; init; }
        public int Channel { get; init; }
        public int Ce { get; init; }
        public int Lun { get; init; }
    }
}
