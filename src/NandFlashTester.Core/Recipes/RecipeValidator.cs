using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Patterns;

namespace NandFlashTester.Core.Recipes;

public sealed record RecipeValidationResult(bool Ok, Recipe? Recipe, IReadOnlyList<string> Errors)
{
    public static RecipeValidationResult Reject(IReadOnlyList<string> errors) =>
        new(false, null, errors);
}

public static partial class RecipeValidator
{
    public const int MaxBytes = 16 * 1024;

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "scripts", "eval", "exec", "execute", "command", "commands", "shell", "python",
        "code", "ssl", "process", "import", "require", "function", "cmd", "powershell", "bash"
    };

    private static readonly HashSet<string> RootFields =
    [
        "schema", "name", "version", "seed", "paceMs", "topology", "pattern", "target", "inject"
    ];

    private static readonly HashSet<string> TopologyFields =
    [
        "channels", "ces", "luns", "planes", "blocks", "pagesPerBlock", "pageSizeBytes"
    ];

    private static readonly HashSet<string> PatternFields = ["kind", "value"];

    private static readonly HashSet<string> TargetFields =
    [
        "channel", "ce", "lun", "plane", "block", "pageStart", "pageCount"
    ];

    private static readonly HashSet<string> InjectFields = ["mode", "page", "byteOffset", "bit"];

    public static RecipeValidationResult Validate(ReadOnlySpan<byte> utf8)
    {
        var errors = new List<string>();
        if (utf8.Length == 0)
        {
            return RecipeValidationResult.Reject(["配方为空，未执行。"]);
        }

        if (utf8.Length > MaxBytes)
        {
            return RecipeValidationResult.Reject(["配方超过 16 KiB，未执行。"]);
        }

        if (utf8.IndexOf((byte)0) >= 0)
        {
            return RecipeValidationResult.Reject(["配方含有空字节，未执行。"]);
        }

        JObject root;
        try
        {
            root = JsonWalk.Read(utf8, errors) ?? new JObject([]);
        }
        catch (JsonException)
        {
            if (errors.Count == 0)
            {
                errors.Add("配方不是合法 JSON，未执行。");
            }

            return RecipeValidationResult.Reject(errors);
        }

        if (errors.Count > 0)
        {
            return RecipeValidationResult.Reject(errors);
        }

        Bind(root, errors, out var recipe);
        return errors.Count > 0 || recipe is null
            ? RecipeValidationResult.Reject(errors)
            : new RecipeValidationResult(true, recipe, []);
    }

    private static void Bind(JObject root, List<string> errors, out Recipe? recipe)
    {
        recipe = null;
        RejectUnknown(root, "$", RootFields, errors);

        var schema = RequireString(root, "schema", "$", errors);
        var name = RequireString(root, "name", "$", errors);
        var version = RequireString(root, "version", "$", errors);
        var seed = RequireUInt32(root, "seed", "$", errors);
        var paceMs = RequireOptionalInt(root, "paceMs", "$", 0, 2000, 0, errors);

        if (schema is not null && schema != RecipeContract.SchemaId)
        {
            errors.Add($"字段 $.schema 必须是 {RecipeContract.SchemaId}，未执行。");
        }

        if (version is not null && version != RecipeContract.ApprovedVersion)
        {
            errors.Add($"字段 $.version 不是已批准版本 {RecipeContract.ApprovedVersion}，未执行。");
        }

        if (name is not null && !NameRegex().IsMatch(name))
        {
            errors.Add("字段 $.name 只允许 1 到 64 个字母、数字、汉字、空格、下划线或连字符，未执行。");
        }

        if (!TryObject(root, "topology", "$", errors, out var topologyNode)
            || !TryObject(root, "pattern", "$", errors, out var patternNode)
            || !TryObject(root, "target", "$", errors, out var targetNode)
            || !TryObject(root, "inject", "$", errors, out var injectNode))
        {
            return;
        }

        RejectUnknown(topologyNode, "$.topology", TopologyFields, errors);
        RejectUnknown(patternNode, "$.pattern", PatternFields, errors);
        RejectUnknown(targetNode, "$.target", TargetFields, errors);
        RejectUnknown(injectNode, "$.inject", InjectFields, errors);

        var channels = RequireInt(topologyNode, "channels", "$.topology", 1, 4, errors);
        var ces = RequireInt(topologyNode, "ces", "$.topology", 1, 4, errors);
        var luns = RequireInt(topologyNode, "luns", "$.topology", 1, 2, errors);
        var planes = RequireInt(topologyNode, "planes", "$.topology", 1, 2, errors);
        var blocks = RequireInt(topologyNode, "blocks", "$.topology", 1, 32, errors);
        var pagesPerBlock = RequireInt(topologyNode, "pagesPerBlock", "$.topology", 1, 64, errors);
        var pageSize = RequireInt(topologyNode, "pageSizeBytes", "$.topology", 16, 4096, errors);
        if (pageSize is > 0 && pageSize % 16 != 0)
        {
            errors.Add("字段 $.topology.pageSizeBytes 必须是 16 的倍数，未执行。");
        }

        SimTopology? topology = null;
        if (errors.Count == 0 && channels is not null && ces is not null && luns is not null
            && planes is not null && blocks is not null && pagesPerBlock is not null && pageSize is not null)
        {
            topology = new SimTopology(channels.Value, ces.Value, luns.Value, planes.Value, blocks.Value, pagesPerBlock.Value, pageSize.Value);
            if (topology.TotalBytes > SimulatedNandDevice.MaxMediaBytes)
            {
                errors.Add("模拟介质合计超过 1 MiB，未执行。这不是千片级拓扑验收。");
                topology = null;
            }
        }

        var kindText = RequireString(patternNode, "kind", "$.pattern", errors);
        PatternKind? kind = kindText switch
        {
            "increment" => PatternKind.Increment,
            "constant" => PatternKind.Constant,
            "prng" => PatternKind.Prng,
            null => null,
            _ => null
        };
        if (kindText is not null && kind is null)
        {
            errors.Add("字段 $.pattern.kind 只允许 increment、constant 或 prng，未执行。");
        }

        byte constantValue = 0;
        if (kind == PatternKind.Constant)
        {
            var value = RequireInt(patternNode, "value", "$.pattern", 0, 255, errors);
            if (value is not null)
            {
                constantValue = (byte)value.Value;
            }
        }
        else if (patternNode.Properties.ContainsKey("value"))
        {
            errors.Add("字段 $.pattern.value 只允许出现在 constant 图案中，未执行。");
        }

        var channel = RequireInt(targetNode, "channel", "$.target", 0, 3, errors);
        var ce = RequireInt(targetNode, "ce", "$.target", 0, 3, errors);
        var lun = RequireInt(targetNode, "lun", "$.target", 0, 1, errors);
        var plane = RequireInt(targetNode, "plane", "$.target", 0, 1, errors);
        var block = RequireInt(targetNode, "block", "$.target", 0, 31, errors);
        var pageStart = RequireInt(targetNode, "pageStart", "$.target", 0, 63, errors);
        var pageCount = RequireInt(targetNode, "pageCount", "$.target", 1, 64, errors);

        if (topology is not null)
        {
            Range(channel, topology.Channels, "$.target.channel", errors);
            Range(ce, topology.Ces, "$.target.ce", errors);
            Range(lun, topology.Luns, "$.target.lun", errors);
            Range(plane, topology.Planes, "$.target.plane", errors);
            Range(block, topology.Blocks, "$.target.block", errors);
            if (pageStart is not null && pageCount is not null
                && (long)pageStart.Value + pageCount.Value > topology.PagesPerBlock)
            {
                errors.Add("目标页范围超出块内页数，未执行。");
            }
        }

        var modeText = RequireString(injectNode, "mode", "$.inject", errors);
        var inject = InjectSpec.None;
        if (modeText == "none")
        {
            if (injectNode.Properties.ContainsKey("page")
                || injectNode.Properties.ContainsKey("byteOffset")
                || injectNode.Properties.ContainsKey("bit"))
            {
                errors.Add("注入模式为 none 时不能携带定位字段，未执行。");
            }
        }
        else if (modeText == "bit_flip")
        {
            var page = RequireInt(injectNode, "page", "$.inject", 0, 63, errors);
            var byteOffset = RequireInt(injectNode, "byteOffset", "$.inject", 0, 4095, errors);
            var bit = RequireInt(injectNode, "bit", "$.inject", 0, 7, errors);
            if (page is not null && byteOffset is not null && bit is not null)
            {
                inject = new InjectSpec(InjectMode.BitFlip, page.Value, byteOffset.Value, bit.Value);
            }

            if (pageStart is not null && pageCount is not null && page is not null
                && (page.Value < pageStart.Value || page.Value >= pageStart.Value + pageCount.Value))
            {
                errors.Add("注入页不在本次写入范围内，未执行。");
            }

            if (pageSize is not null && byteOffset is not null && byteOffset.Value >= pageSize.Value)
            {
                errors.Add("注入字节偏移超出页大小，未执行。");
            }
        }
        else if (modeText is not null)
        {
            errors.Add("字段 $.inject.mode 只允许 none 或 bit_flip，未执行。");
        }

        if (errors.Count > 0 || topology is null || kind is null || name is null || version is null
            || seed is null || paceMs is null || channel is null || ce is null || lun is null
            || plane is null || block is null || pageStart is null || pageCount is null)
        {
            return;
        }

        var bound = new Recipe(
            name,
            version,
            seed.Value,
            paceMs.Value,
            topology,
            new PatternSpec(kind.Value, constantValue),
            new TargetSpec(channel.Value, ce.Value, lun.Value, plane.Value, block.Value, pageStart.Value, pageCount.Value),
            inject,
            "",
            "");
        var canonical = Canonical(bound);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        recipe = bound with { CanonicalJson = canonical, Sha256 = hash };
    }

    public static string Canonical(Recipe recipe)
    {
        var pattern = recipe.Pattern.Kind == PatternKind.Constant
            ? FormattableString.Invariant($"{{\"kind\":\"constant\",\"value\":{recipe.Pattern.ConstantValue}}}")
            : $"{{\"kind\":\"{recipe.Pattern.KindText}\"}}";
        var inject = recipe.Inject.Mode == InjectMode.BitFlip
            ? FormattableString.Invariant(
                $"{{\"mode\":\"bit_flip\",\"page\":{recipe.Inject.Page},\"byteOffset\":{recipe.Inject.ByteOffset},\"bit\":{recipe.Inject.Bit}}}")
            : "{\"mode\":\"none\"}";
        var topology = recipe.Topology;
        var target = recipe.Target;
        return FormattableString.Invariant(
            $"{{\"schema\":\"{RecipeContract.SchemaId}\",\"name\":{JsonSerializer.Serialize(recipe.Name)},\"version\":\"{recipe.Version}\",\"seed\":{recipe.Seed},\"paceMs\":{recipe.PaceMs},\"topology\":{{\"channels\":{topology.Channels},\"ces\":{topology.Ces},\"luns\":{topology.Luns},\"planes\":{topology.Planes},\"blocks\":{topology.Blocks},\"pagesPerBlock\":{topology.PagesPerBlock},\"pageSizeBytes\":{topology.PageSizeBytes}}},\"pattern\":{pattern},\"target\":{{\"channel\":{target.Channel},\"ce\":{target.Ce},\"lun\":{target.Lun},\"plane\":{target.Plane},\"block\":{target.Block},\"pageStart\":{target.PageStart},\"pageCount\":{target.PageCount}}},\"inject\":{inject}}}");
    }

    private static void Range(int? value, int limit, string path, List<string> errors)
    {
        if (value is not null && value.Value >= limit)
        {
            errors.Add($"字段 {path} 超出当前拓扑，未执行。");
        }
    }

    private static void RejectUnknown(JObject obj, string path, HashSet<string> allowed, List<string> errors)
    {
        foreach (var key in obj.Properties.Keys)
        {
            if (!allowed.Contains(key) && !Forbidden.Contains(key))
            {
                errors.Add($"字段 {path}.{key} 不在白名单内，未执行。");
            }
        }
    }

    private static bool TryObject(JObject parent, string key, string path, List<string> errors, out JObject obj)
    {
        if (!parent.Properties.TryGetValue(key, out var value))
        {
            errors.Add($"缺少字段 {path}.{key}，未执行。");
            obj = new JObject([]);
            return false;
        }

        if (value is not JObject nested)
        {
            errors.Add($"字段 {path}.{key} 必须是对象，未执行。");
            obj = new JObject([]);
            return false;
        }

        obj = nested;
        return true;
    }

    private static string? RequireString(JObject obj, string key, string path, List<string> errors)
    {
        if (!obj.Properties.TryGetValue(key, out var value))
        {
            errors.Add($"缺少字段 {path}.{key}，未执行。");
            return null;
        }

        if (value is not JString text)
        {
            errors.Add($"字段 {path}.{key} 必须是字符串，未执行。");
            return null;
        }

        return text.Value;
    }

    private static int? RequireInt(JObject obj, string key, string path, int min, int max, List<string> errors)
    {
        if (!obj.Properties.TryGetValue(key, out var value))
        {
            errors.Add($"缺少字段 {path}.{key}，未执行。");
            return null;
        }

        if (value is not JNumber number)
        {
            errors.Add($"字段 {path}.{key} 必须是整数，未执行。");
            return null;
        }

        if (number.Integer is null || number.Integer < min || number.Integer > max)
        {
            errors.Add($"字段 {path}.{key} 超出允许范围 {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}，未执行。");
            return null;
        }

        return (int)number.Integer.Value;
    }

    private static int? RequireOptionalInt(JObject obj, string key, string path, int min, int max, int fallback, List<string> errors)
    {
        if (!obj.Properties.ContainsKey(key))
        {
            return fallback;
        }

        return RequireInt(obj, key, path, min, max, errors);
    }

    private static uint? RequireUInt32(JObject obj, string key, string path, List<string> errors)
    {
        if (!obj.Properties.TryGetValue(key, out var value))
        {
            errors.Add($"缺少字段 {path}.{key}，未执行。");
            return null;
        }

        if (value is not JNumber number || number.Integer is null || number.Integer < 0 || number.Integer > uint.MaxValue)
        {
            errors.Add($"字段 {path}.{key} 必须是 0 到 4294967295 的整数，未执行。");
            return null;
        }

        return (uint)number.Integer.Value;
    }

    [GeneratedRegex(@"\A[\p{L}\p{N}_ -]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    private abstract record JValue;

    private sealed record JObject(Dictionary<string, JValue> Properties) : JValue;

    private sealed record JString(string Value) : JValue;

    private sealed record JNumber(long? Integer) : JValue;

    private sealed record JInvalid : JValue;

    private static class JsonWalk
    {
        public static JObject? Read(ReadOnlySpan<byte> utf8, List<string> errors)
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });

            if (!reader.Read())
            {
                errors.Add("配方为空，未执行。");
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                errors.Add("配方根节点必须是对象，未执行。");
                return null;
            }

            var root = ReadObject(ref reader, "$", errors);
            if (root is not null && reader.Read())
            {
                errors.Add("配方 JSON 末尾有多余内容，未执行。");
            }

            return root;
        }

        private static JObject? ReadObject(ref Utf8JsonReader reader, string path, List<string> errors)
        {
            var map = new Dictionary<string, JValue>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return new JObject(map);
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    errors.Add($"{path} 的 JSON 结构无效，未执行。");
                    return null;
                }

                var name = reader.GetString() ?? "";
                var childPath = path + "." + name;
                if (!reader.Read())
                {
                    errors.Add($"字段 {childPath} 缺少值，未执行。");
                    return null;
                }

                if (map.ContainsKey(name))
                {
                    errors.Add($"字段 {childPath} 重复，未执行。");
                }

                if (Forbidden.Contains(name))
                {
                    errors.Add($"字段 {childPath} 被拒绝：受限配方不能携带脚本、命令或可执行内容，未执行。");
                }

                var value = ReadValue(ref reader, childPath, errors);
                if (value is null)
                {
                    return null;
                }

                map[name] = value;
            }

            errors.Add($"{path} 的对象没有结束，未执行。");
            return null;
        }

        private static JValue? ReadValue(ref Utf8JsonReader reader, string path, List<string> errors)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    return ReadObject(ref reader, path, errors);
                case JsonTokenType.StartArray:
                    errors.Add($"{path} 不允许数组，未执行。");
                    reader.Skip();
                    return new JInvalid();
                case JsonTokenType.String:
                    return new JString(reader.GetString() ?? "");
                case JsonTokenType.Number:
                    var raw = Encoding.UTF8.GetString(reader.ValueSpan);
                    if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || raw.Contains('+'))
                    {
                        errors.Add($"{path} 必须是整数，未执行。");
                        return new JInvalid();
                    }

                    if (!reader.TryGetInt64(out var integer))
                    {
                        errors.Add($"{path} 整数超出范围，未执行。");
                        return new JInvalid();
                    }

                    return new JNumber(integer);
                case JsonTokenType.True:
                case JsonTokenType.False:
                    errors.Add($"{path} 不允许布尔值，未执行。");
                    return new JInvalid();
                case JsonTokenType.Null:
                    errors.Add($"{path} 不允许 null，未执行。");
                    return new JInvalid();
                default:
                    errors.Add($"{path} 的 JSON 结构无效，未执行。");
                    return null;
            }
        }
    }
}
