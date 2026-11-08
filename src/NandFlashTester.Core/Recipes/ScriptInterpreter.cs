using System.Diagnostics;
using NandFlashTester.Core.Patterns;
using NandFlashTester.Core.Platform;

namespace NandFlashTester.Core.Recipes;

public sealed record ScriptLimits(int MaxSteps, int MaxStoredBytes, TimeSpan MaxDuration)
{
    public static ScriptLimits Default { get; } = new(ScriptCompiler.MaxExpanded, 64 * IsolatedPageDriver.PageSize, TimeSpan.FromSeconds(2));
}

public sealed record ScriptStepLog(
    int Index,
    int Line,
    string Op,
    string State,
    string Message,
    int? ErrorBitCount,
    bool HardwareExecuted);

public sealed record ScriptRun(
    string EvidenceClass,
    bool HardwareExecuted,
    bool Measured,
    int? ErrorBitCount,
    string ErrorCountText,
    int BytesEmitted,
    IReadOnlyList<ScriptStepLog> Steps,
    string StopReason);

public interface IScriptDriver
{
    string EvidenceClass { get; }

    bool AllowsMedia { get; }

    int EmitCount { get; }

    string? Refuse(string op);

    void Erase(int block);

    void Program(int block, int page, byte[] data);

    byte[] Read(int block, int page);
}

public sealed class BlockedScriptDriver : IScriptDriver
{
    private readonly BlockedHardwareTransport _transport = new();

    public string EvidenceClass => EvidenceClasses.SoftwareContract;

    public bool AllowsMedia => false;

    public int EmitCount => _transport.EmitCount;

    public string? Refuse(string op)
    {
        var outcome = SessionEngine.BlockBeforeSend(op, _transport);
        if (outcome is null || outcome.Sent || outcome.HardwareAccepted || _transport.EmitCount != 0)
        {
            throw new InvalidOperationException("配置计划在发送前没有停下。");
        }

        return outcome.Reason;
    }

    public void Erase(int block) => throw new InvalidOperationException("未连接介质，不能擦除。");

    public void Program(int block, int page, byte[] data) => throw new InvalidOperationException("未连接介质，不能写入。");

    public byte[] Read(int block, int page) => throw new InvalidOperationException("未连接介质，不能读回。");
}

public sealed class IsolatedPageDriver : IScriptDriver
{
    public const int PageSize = 16;
    private readonly Dictionary<(int Block, int Page), byte[]> _pages = new();
    private readonly (int Block, int Page, int Offset, int Bit)? _flip;

    public IsolatedPageDriver((int Block, int Page, int Offset, int Bit)? flip = null) => _flip = flip;

    public string EvidenceClass => EvidenceClasses.AutomatedTest;

    public bool AllowsMedia => true;

    public int EmitCount => 0;

    public string? Refuse(string op) => null;

    public void Erase(int block)
    {
        foreach (var key in _pages.Keys.Where(key => key.Block == block).ToArray())
        {
            _pages.Remove(key);
        }
    }

    public void Program(int block, int page, byte[] data)
    {
        if (data.Length != PageSize)
        {
            throw new InvalidOperationException("隔离介质只接受固定页长。");
        }

        var key = (block, page);
        if (!_pages.ContainsKey(key) && _pages.Count >= 64)
        {
            throw new ScriptLimitException("隔离介质页数超过 64。");
        }

        _pages[key] = data.ToArray();
    }

    public byte[] Read(int block, int page)
    {
        var data = _pages.TryGetValue((block, page), out var stored) ? stored.ToArray() : Enumerable.Repeat((byte)0xFF, PageSize).ToArray();
        if (_flip is { } flip && flip.Block == block && flip.Page == page && flip.Offset >= 0 && flip.Offset < data.Length && flip.Bit is >= 0 and < 8)
        {
            data[flip.Offset] ^= (byte)(1 << flip.Bit);
        }

        return data;
    }
}

public sealed class ScriptLimitException(string message) : Exception(message);

public static class ScriptInterpreter
{
    public static ScriptRun Run(ScriptPlan plan, IScriptDriver driver, ScriptLimits limits, Func<TimeSpan>? elapsed = null, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        Func<TimeSpan> now = elapsed ?? (() => started.Elapsed);
        var steps = new List<ScriptStepLog>();
        var errorBits = 0;
        var compared = false;
        string? stop = null;

        foreach (var action in plan.Actions)
        {
            if (steps.Count >= limits.MaxSteps)
            {
                stop = "步数达到上限，后续步骤没有执行。";
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                stop = "停止发生在后续步骤之前。";
                break;
            }

            if (now() > limits.MaxDuration)
            {
                stop = "超过时间上限，后续步骤没有执行。";
                break;
            }

            var refusal = driver.Refuse(action.Op);
            if (refusal is not null)
            {
                steps.Add(new ScriptStepLog(action.Index, action.Line, action.Op, "不可执行", refusal, null, false));
                continue;
            }

            try
            {
                var bits = Apply(plan, action, driver);
                if (bits is not null)
                {
                    compared = true;
                    errorBits += bits.Value;
                }

                steps.Add(new ScriptStepLog(action.Index, action.Line, action.Op, "测试介质", bits is null ? "隔离测试介质已记录这一步。不是实测。" : $"隔离测试介质比较出 {bits.Value} 个不一致位。不是实测。", bits, false));
            }
            catch (ScriptLimitException ex)
            {
                stop = ex.Message;
                steps.Add(new ScriptStepLog(action.Index, action.Line, action.Op, "已停止", ex.Message, null, false));
                break;
            }
        }

        int? reportedBits = driver.AllowsMedia && compared ? errorBits : null;
        var text = reportedBits is null ? "未统计" : reportedBits.Value.ToString();
        return new ScriptRun(driver.EvidenceClass, false, false, reportedBits, text, driver.EmitCount, steps, stop ?? "计划走完。硬件未执行。");
    }

    private static int? Apply(ScriptPlan plan, ScriptAction action, IScriptDriver driver)
    {
        switch (action.Op)
        {
            case "erase":
                driver.Erase(action.Block);
                return null;
            case "program":
                driver.Program(action.Block, action.Page ?? 0, PatternGenerator.Generate(PatternKind.Increment, IsolatedPageDriver.PageSize, plan.Seed, action.Block, action.Page ?? 0));
                return null;
            case "read":
                _ = driver.Read(action.Block, action.Page ?? 0);
                return null;
            case "compare":
                var expected = PatternGenerator.Generate(PatternKind.Increment, IsolatedPageDriver.PageSize, plan.Seed, action.Block, action.Page ?? 0);
                var actual = driver.Read(action.Block, action.Page ?? 0);
                return CountBits(expected, actual);
            default:
                throw new ScriptLimitException($"步骤 {action.Op} 不能执行。");
        }
    }

    private static int CountBits(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var bits = 0;
        var length = Math.Min(expected.Length, actual.Length);
        for (var i = 0; i < length; i++)
        {
            var xor = (byte)(expected[i] ^ actual[i]);
            while (xor != 0)
            {
                bits += xor & 1;
                xor >>= 1;
            }
        }

        bits += Math.Abs(expected.Length - actual.Length) * 8;
        return bits;
    }
}
