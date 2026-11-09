using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NandFlashTester.Core.Management;

public static class DraftReadOnlyProfile
{
    public const string Version = "draft-1";
    public const string Label = "草案只读";
    public const int MaxJsonBytes = 65536;
    public static readonly TimeSpan FrameLimit = TimeSpan.FromSeconds(5);
    public const bool LiveConnectAllowed = false;
    public const string LiveConnectReason = "草案只读。正式端点、证书和实机连接未提供，默认禁止连接。";
    public static readonly string[] Operations = ["hello", "status"];
    public static readonly string[] NotInThisDraft = ["start", "stop", "reset", "permit", "result-query", "recover"];
}

public static class DraftFrames
{
    public static byte[] Encode(ReadOnlySpan<byte> json)
    {
        if (json.Length is < 1 or > DraftReadOnlyProfile.MaxJsonBytes)
        {
            throw new InvalidOperationException("草案帧长度必须在 1 到 65536，且只计算 JSON。");
        }

        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF)
        {
            throw new InvalidOperationException("草案帧拒绝 BOM。");
        }

        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)json.Length);
        json.CopyTo(frame.AsSpan(4));
        return frame;
    }

    public static bool TryRead(ReadOnlySpan<byte> buffer, out int consumed, out byte[]? json, out string? error)
    {
        consumed = 0;
        json = null;
        error = null;
        if (buffer.Length < 4)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        if (length is < 1 or > DraftReadOnlyProfile.MaxJsonBytes)
        {
            error = "长度前缀非法。未按声明长度分配载荷。";
            consumed = 4;
            return false;
        }

        if (buffer.Length < 4 + length)
        {
            return false;
        }

        json = buffer.Slice(4, (int)length).ToArray();
        consumed = 4 + (int)length;
        return true;
    }

    public static async Task<byte[]?> ReadOneAsync(Stream stream, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            var prefix = new byte[4];
            await ReadExactAsync(stream, prefix, timeout.Token);
            var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (length is < 1 or > DraftReadOnlyProfile.MaxJsonBytes)
            {
                throw new InvalidOperationException("长度前缀非法。未按声明长度分配载荷。");
            }

            var payload = new byte[length];
            await ReadExactAsync(stream, payload, timeout.Token);
            return payload;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("草案收帧超过时限，已停止。");
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new InvalidOperationException("草案帧不完整。");
            }

            offset += read;
        }
    }
}

public sealed record DraftHelloSuccess(string DeviceId, string BootId, string? FirmwareVersion, IReadOnlyList<string> Operations, IReadOnlyList<double> QualifiedRates);

public sealed record DraftStatusSuccess(string BootId, long Revision, string State, string? SessionId, bool SafeToStart, bool SafeToReset, int RateCount, IReadOnlyList<string> Uncovered);

public static class DraftMessages
{
    public static byte[] HelloRequest(string clientId, string requestId) =>
        Json(new { version = DraftReadOnlyProfile.Version, kind = "request", request_id = requestId, op = "hello", idempotency_key = (string?)null, payload = new { supported_versions = new[] { DraftReadOnlyProfile.Version }, client_id = clientId } });

    public static byte[] HelloSuccess(string requestId, string deviceId, string bootId) =>
        Json(new { version = DraftReadOnlyProfile.Version, kind = "success", request_id = requestId, op = "hello", payload = new { selected_version = DraftReadOnlyProfile.Version, device_id = deviceId, boot_id = bootId, firmware_version = (string?)null, operations = DraftReadOnlyProfile.Operations, max_frame_bytes = DraftReadOnlyProfile.MaxJsonBytes, qualified_rates_mt_s = Array.Empty<double>(), data_plane = "not_defined_here" } });

    public static byte[] StatusRequest(string requestId)
    {
        var json = "{\"version\":\"draft-1\",\"kind\":\"request\",\"request_id\":" + JsonSerializer.Serialize(requestId) + ",\"op\":\"status\",\"idempotency_key\":null,\"payload\":{}}";
        return Encoding.UTF8.GetBytes(json);
    }

    public static byte[] StatusSuccessIdle(string requestId, string bootId) =>
        Json(new { version = DraftReadOnlyProfile.Version, kind = "success", request_id = requestId, op = "status", payload = new { boot_id = bootId, revision = 1, state = "idle", session_id = (string?)null, safe_to_start = false, safe_to_reset = false, rates = Array.Empty<object>(), summary_uncovered = Array.Empty<string>() } });

    public static object Parse(ReadOnlySpan<byte> json)
    {
        RejectStructure(json);
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        var op = RequiredString(root, "op");
        var kind = RequiredString(root, "kind");
        var requestFields = kind == "request"
            ? new[] { "version", "kind", "request_id", "op", "payload", "idempotency_key" }
            : new[] { "version", "kind", "request_id", "op", "payload" };
        RequireOnly(root, requestFields);
        if (op is not ("hello" or "status"))
        {
            throw new InvalidOperationException($"操作 {op} 不在草案只读子集内，没有编成生产命令。");
        }

        if (RequiredString(root, "version") != DraftReadOnlyProfile.Version)
        {
            throw new InvalidOperationException("未知草案版本，不能进入设备操作。");
        }

        var payload = RequiredObject(root, "payload");
        if (op == "hello" && kind == "success")
        {
            RequireOnly(payload, "selected_version", "device_id", "boot_id", "firmware_version", "operations", "max_frame_bytes", "qualified_rates_mt_s", "data_plane");
            if (payload.GetProperty("max_frame_bytes").GetInt32() != DraftReadOnlyProfile.MaxJsonBytes || RequiredString(payload, "data_plane") != "not_defined_here")
            {
                throw new InvalidOperationException("草案协商里的帧长或数据面与只读子集不一致。");
            }

            var operations = StringArray(payload, "operations");
            if (operations.Any(item => item is not ("hello" or "status")))
            {
                throw new InvalidOperationException("协商结果含草案以外的操作，已拒绝。");
            }

            var rates = NumberArray(payload, "qualified_rates_mt_s");
            return new DraftHelloSuccess(RequiredString(payload, "device_id"), RequiredString(payload, "boot_id"), OptionalString(payload, "firmware_version"), operations, rates);
        }

        if (op == "status" && kind == "success")
        {
            RequireOnly(payload, "boot_id", "revision", "state", "session_id", "safe_to_start", "safe_to_reset", "rates", "summary_uncovered");
            if (RequiredString(payload, "state") is not ("idle" or "running" or "stopping" or "fault" or "recovering"))
            {
                throw new InvalidOperationException("草案状态值不在只读子集内。");
            }

            foreach (var rate in payload.GetProperty("rates").EnumerateArray())
            {
                RequireOnly(rate, "scope_id", "requested_mt_s", "actual_mt_s", "evidence", "downgraded", "reason", "coverage", "point_id");
                var evidence = RequiredString(rate, "evidence");
                var actual = rate.GetProperty("actual_mt_s");
                if (evidence is "unverified" or "configured_readback" && actual.ValueKind != JsonValueKind.Null)
                {
                    throw new InvalidOperationException("配置回读或未核实证据不能带实测速率。");
                }

                if (evidence == "measured")
                {
                    throw new InvalidOperationException("本草案包不能把消息里的 measured 当成真实测量。");
                }
            }

            return new DraftStatusSuccess(
                RequiredString(payload, "boot_id"),
                payload.GetProperty("revision").GetInt64(),
                RequiredString(payload, "state"),
                OptionalString(payload, "session_id"),
                payload.GetProperty("safe_to_start").GetBoolean(),
                payload.GetProperty("safe_to_reset").GetBoolean(),
                payload.GetProperty("rates").GetArrayLength(),
                payload.GetProperty("summary_uncovered").EnumerateArray().Select(item => item.GetString() ?? "").ToArray());
        }

        if (kind == "request" && op is "hello" or "status")
        {
            if (op == "hello")
            {
                RequireOnly(payload, "supported_versions", "client_id");
            }
            else
            {
                RequireOnly(payload);
            }

            return op;
        }

        throw new InvalidOperationException("草案消息种类无法识别，未执行。");
    }

    public static DraftStatusSuccess LoopbackHelloThenStatus()
    {
        if (DraftReadOnlyProfile.LiveConnectAllowed)
        {
            throw new InvalidOperationException(DraftReadOnlyProfile.LiveConnectReason);
        }

        var helloFrame = DraftFrames.Encode(HelloRequest("draft-fixture-client", "draft-hello"));
        var helloJson = ReadSingle(helloFrame);
        if (Parse(helloJson) is not string helloOp || helloOp != "hello")
        {
            throw new InvalidOperationException("协商请求没有先于状态查询。");
        }

        var hello = (DraftHelloSuccess)Parse(HelloSuccess("draft-hello", "draft-fixture", "draft-boot"));
        if (hello.FirmwareVersion is not null || hello.QualifiedRates.Count != 0)
        {
            throw new InvalidOperationException("隔离替身不得填入固件版本或合格速率。");
        }

        var statusFrame = DraftFrames.Encode(StatusRequest("draft-status"));
        _ = ReadSingle(statusFrame);
        var status = (DraftStatusSuccess)Parse(StatusSuccessIdle("draft-status", hello.BootId));
        return status;
    }

    private static byte[] ReadSingle(byte[] frame)
    {
        if (!DraftFrames.TryRead(frame, out var consumed, out var json, out var error) || json is null)
        {
            throw new InvalidOperationException(error ?? "草案帧无法解析。");
        }

        if (consumed != frame.Length)
        {
            throw new InvalidOperationException("草案帧后有尾随内容。");
        }

        return json;
    }

    private static byte[] Json(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        return bytes;
    }

    private static void RejectStructure(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidOperationException("草案消息必须是 JSON 对象。");
        }

        ReadObject(ref reader, 1);
        if (reader.Read())
        {
            throw new InvalidOperationException("草案 JSON 末尾有多余内容。");
        }
    }

    private static void ReadObject(ref Utf8JsonReader reader, int depth)
    {
        if (depth > 16)
        {
            throw new InvalidOperationException("草案 JSON 超过 16 层。");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new InvalidOperationException("草案 JSON 结构无效。");
            }

            var name = reader.GetString() ?? "";
            if (!seen.Add(name))
            {
                throw new InvalidOperationException($"草案字段 {name} 重复。");
            }

            if (!reader.Read())
            {
                throw new InvalidOperationException("草案字段缺少值。");
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                ReadObject(ref reader, depth + 1);
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                ReadArray(ref reader, depth + 1);
            }
            else if (reader.TokenType == JsonTokenType.Number)
            {
                var raw = Encoding.UTF8.GetString(reader.ValueSpan);
                if (raw.Contains("NaN", StringComparison.Ordinal) || raw.Contains("Infinity", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("草案拒绝非有限数。");
                }
            }
        }

        throw new InvalidOperationException("草案对象没有结束。");
    }

    private static void ReadArray(ref Utf8JsonReader reader, int depth)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                ReadObject(ref reader, depth + 1);
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                ReadArray(ref reader, depth + 1);
            }
        }
    }

    private static void RequireOnly(JsonElement element, params string[] names)
    {
        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidOperationException($"草案未知字段 {property.Name}，没有据此编造命令。");
            }
        }

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out _))
            {
                throw new InvalidOperationException($"缺少 {name}。");
            }
        }
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new InvalidOperationException($"缺少 {name}。");

    private static string? OptionalString(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();

    private static JsonElement RequiredObject(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{name} 必须是对象。");
        }

        return value;
    }

    private static List<string> StringArray(JsonElement element, string name) =>
        element.GetProperty(name).EnumerateArray().Select(item => item.GetString() ?? "").ToList();

    private static List<double> NumberArray(JsonElement element, string name) =>
        element.GetProperty(name).EnumerateArray().Select(item => item.GetDouble()).ToList();
}

internal static class DraftCulture
{
    public static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
