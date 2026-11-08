namespace NandFlashTester.Core.Platform;

public static class PlatformModes
{
    public const string Production = "Production";
    public const string AutomatedTest = "AutomatedTest";

    public static bool IsAutomatedTest(string? mode) =>
        string.Equals(mode, AutomatedTest, StringComparison.Ordinal);
}

public static class EvidenceClasses
{
    public const string SoftwareContract = "软件契约";
    public const string AutomatedTest = "自动化测试";
}

public sealed record IcdGap(string Id, string NeededFor, string Missing, string Effect);

public static class IcdGapCatalog
{
    public static IReadOnlyList<IcdGap> Missing { get; } =
    [
        new("icd-excerpt", "任何实机收发", "获准云端使用的脱敏新平台协议摘录还没有提供。内部 ICD 原件未读取。", "禁止形成或发送控制载荷。"),
        new("link-endpoint", "打开设备", "实际连接端点未提供。", "不假设端口、总线、地址或设备路径。"),
        new("framing", "收发帧", "帧界、长度、校验和字节序未提供。", "不自拟线上帧。"),
        new("command-map", "擦除、写入、读回、身份、取消", "逻辑意图到线上命令的映射未提供。", "不自拟操作码或控制载荷，也不声称符合硬件。"),
        new("status-codes", "命令完成", "设备状态字和错误码未提供。", "主机超时只代表软件等待结束，不是设备状态。"),
        new("data-plane", "页数据", "载荷如何分包、对齐和确认未提供。", "不发送页数据。"),
        new("capability-query", "能力协商", "线上能力查询方法未提供。", "能力保持未知，不填实测速率或器件参数。"),
        new("firmware-identity", "版本兼容", "固件版本格式和兼容规则未提供。", "不读取、不刷写固件。"),
        new("rate-semantics", "速率", "速率字段的含义和实测来源未提供。", "不把设置值写成已测速率。"),
        new("partial-recovery", "断点恢复", "在途命令的部分恢复语义未提供。", "重启后只把未完成会话标为已中断，不续跑。"),
        new("vendor-sdk", "驱动接入", "供应方 SDK 或 x64 驱动包未提供。", "不链接未知驱动。"),
        new("driver-signing", "Windows 驱动安装", "驱动签名、安装权限和供应方依赖未提供。", "上位机不安装驱动。"),
        new("safety-interlock", "电压、固件、Handler、温箱", "安全互锁条件未提供。", "这些操作禁用，不会下发。"),
        new("prototype-evidence", "实机验收", "样机交付和 Windows 执行证据未提供。", "不能标实机通过或 Windows 执行通过。")
    ];
}

public static class LogicalIntents
{
    public static readonly string[] RecipeSteps = ["identify", "erase", "program", "read", "compare"];
    public static readonly string[] Dangerous = ["set-voltage", "firmware-update", "handler-move", "thermal-set"];

    public static bool IsDangerous(string intent) => Dangerous.Contains(intent, StringComparer.Ordinal);

    public static string Text(string intent) => intent switch
    {
        "identify" => "读取身份",
        "erase" => "擦除",
        "program" => "写入",
        "read" => "读回",
        "compare" => "比较",
        "set-voltage" => "电压",
        "firmware-update" => "固件",
        "handler-move" => "Handler",
        "thermal-set" => "温箱",
        "cancel" => "取消",
        _ => intent
    };
}

public sealed record TransportReply(bool Ok, string Summary);

public interface ITransport
{
    bool MayEmitBytes { get; }

    int EmitCount { get; }

    Task<TransportReply> ExchangeAsync(string intent, CancellationToken cancellationToken);
}

public sealed class BlockedHardwareTransport : ITransport
{
    public bool MayEmitBytes => false;

    public int EmitCount => 0;

    public Task<TransportReply> ExchangeAsync(string intent, CancellationToken cancellationToken) =>
        Task.FromException<TransportReply>(new InvalidOperationException("协议摘录未到，硬件传输禁止收发。"));
}

public sealed record CommandOutcome(
    string Intent,
    string State,
    bool Sent,
    bool HardwareAccepted,
    string EvidenceClass,
    string Reason,
    string NextStep)
{
    public string IntentText => LogicalIntents.Text(Intent);

    public string StateText => State switch
    {
        "Rejected" => "未发送",
        "Succeeded" => "测试应答",
        "Failed" => "失败",
        "TimedOut" => "超时",
        "Cancelled" => "已取消",
        _ => State
    };
}

public static class SessionEngine
{
    public static CommandOutcome? BlockBeforeSend(string intent, ITransport transport)
    {
        if (LogicalIntents.IsDangerous(intent))
        {
            return new CommandOutcome(
                intent,
                "Rejected",
                false,
                false,
                EvidenceClasses.SoftwareContract,
                "危险操作已禁用。安全互锁和对应协议未提供。",
                "不要重试下发。等安全条件和获准资料到齐后再单独打开。");
        }

        if (!transport.MayEmitBytes)
        {
            return new CommandOutcome(
                intent,
                "Rejected",
                false,
                false,
                EvidenceClasses.SoftwareContract,
                "未发送。获准的新平台协议摘录未提供，软件没有形成控制载荷。",
                "这不是实机通过。等脱敏协议摘录和样机就绪后再验。");
        }

        return null;
    }

    public static async Task<CommandOutcome> RunAsync(
        string intent,
        ITransport transport,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var blocked = BlockBeforeSend(intent, transport);
        if (blocked is not null)
        {
            return blocked;
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            var reply = await transport.ExchangeAsync(intent, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            return new CommandOutcome(
                intent,
                reply.Ok ? "Succeeded" : "Failed",
                true,
                false,
                EvidenceClasses.AutomatedTest,
                reply.Summary,
                "这是自动化测试替身的应答，不能标实机通过。");
        }
        catch (OperationCanceledException)
        {
            var timedOut = !cancellationToken.IsCancellationRequested;
            return new CommandOutcome(
                intent,
                timedOut ? "TimedOut" : "Cancelled",
                true,
                false,
                EvidenceClasses.AutomatedTest,
                timedOut ? "主机等待超时。这不是设备状态字。" : "主机已取消等待。",
                timedOut ? "检查测试替身是否无应答。不要把超时写成实机失败结论。" : "未再发起下一次收发。");
        }
    }

    public static async Task<IReadOnlyList<CommandOutcome>> RunRecipeAsync(
        ITransport transport,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<CommandOutcome>();
        foreach (var intent in LogicalIntents.RecipeSteps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new CommandOutcome(intent, "Cancelled", false, false, transport.MayEmitBytes ? EvidenceClasses.AutomatedTest : EvidenceClasses.SoftwareContract, "取消时这一步还没有发送。", "后续步骤不再发送。"));
                continue;
            }

            outcomes.Add(await RunAsync(intent, transport, timeout, cancellationToken));
            var state = outcomes[^1].State;
            if (state is "TimedOut" or "Cancelled" or "Failed")
            {
                break;
            }
        }

        return outcomes;
    }
}
