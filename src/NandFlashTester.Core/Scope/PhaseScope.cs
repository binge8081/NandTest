namespace NandFlashTester.Core.Scope;

public sealed record ScopeItem(string Id, string SubItem, string Priority, string Bucket, string Status);

/// <summary>
/// 19 号对照的 36 个子行。本程序只标记首个模拟闭环的子集，不把未写的 P0 从一期范围里拿掉。
/// </summary>
public static class PhaseScope
{
    public const bool SimulationOnly = true;
    public const bool ClaimsMeasuredThroughput = false;
    public const bool LocalDeliveryPathWritten = false;
    public const bool JsonEntryIsSslCompatible = false;
    public const string LocalDeliveryTarget = @"D:\NandFlashTester";
    public const string ReimplementationPolicy = "旧能力可以依据合法资料在新平台独立重写，目标是覆盖旧能力并加上本次规划。禁止读取或导入反编译和旧实现。不要求复刻旧架构或旧界面。";
    public const string ExpertEntryLocation = "简单操作和专家受限 JSON 的编辑、校验、执行、进度、结果都在同一测试页。普通用户不必打开独立脚本软件。该 JSON 不是 SSL 兼容。";

    public static IReadOnlyList<ScopeItem> Items { get; } =
    [
        Item("F01", "基础闭环", "P0", "首个模拟闭环", "已实现子集：模拟擦除、图案写入、读回比较、进度、取消、SQLite 与 CSV。不是实机数据正确性验收。"),
        Item("F02", "命令抽象", "P0", "一期P0后续增量", "未实现"),
        Item("F03", "图案", "P0", "首个模拟闭环", "已实现子集：increment、constant、prng。没有文件图案，也没有 FPGA 对照。"),
        Item("F04", "忙时间与应力", "P0", "一期P0后续增量", "未实现"),
        Item("F05", "错误率ECC", "P0", "一期P0后续增量", "未实现"),
        Item("F06", "规模速率", "P0", "一期P0后续增量", "未实现。本闭环的拓扑上限不是 1056/528 量产拓扑，也不填实测速率。"),
        Item("F07", "受限配方", "P0（封装配方）", "首个模拟闭环", "已实现子集：同一测试页里的简单配方，以及受限 JSON 编辑和校验。不是参数库审批，也不是 SSL。"),
        Item("F07", "专业脚本与兼容", "P0（底层/兼容）", "一期P0后续增量", "未实现。旧脚本能力留待按合法公开行为独立重写。本版 JSON 入口不是 SSL 兼容，不读取反编译或旧实现。"),
        Item("F07", "单步调试", "P1（调试单步）", "现行P1", "未实现"),
        Item("F08", "P/E长测恢复", "P0", "一期P0后续增量", "未实现。服务重启只保留已结束任务，不恢复未完成的长测。"),
        Item("F09", "写干扰", "P0", "一期P0后续增量", "未实现"),
        Item("F10", "读干扰", "P0", "一期P0后续增量", "未实现"),
        Item("F11", "设备信息", "P0", "首个模拟闭环", "已实现子集：合成标识、状态和参数页。不是真实器件兼容性。"),
        Item("F12", "基础UI", "P0（基础壳）", "首个模拟闭环", "已实现子集：同一中文测试页包含简单操作、专家 JSON 入口、进度、取消、历史和 CSV。"),
        Item("F12", "完整双模式UI", "P0（产品化）", "一期P0后续增量", "未实现。完整角色矩阵、双语和专家工作台仍保留。本页已有简单操作与受限 JSON，不因此删除本项。"),
        Item("F13", "结构化分布", "P0", "一期P0后续增量", "未实现"),
        Item("F14", "拉偏安全与降档", "P0（已定）", "一期P0后续增量", "未实现。没有电压拉偏。"),
        Item("F15", "校准时序", "P0（已定）", "一期P0后续增量", "未实现"),
        Item("F16", "SKU三温及工业", "P0", "一期P0后续增量", "未实现。没有温箱。"),
        Item("F17", "公开命令最小集", "P0（公开命令最小集）", "一期P0后续增量", "未实现"),
        Item("F17", "完整Vth", "P1（完整Vth）", "现行P1", "未实现"),
        Item("F18", "保持向导", "P1", "现行P1", "未实现"),
        Item("F19", "Retry工作流", "P0", "一期P0后续增量", "未实现"),
        Item("F20", "五级与复测", "P0", "一期P0后续增量", "未实现"),
        Item("F20", "MES接口", "P0", "一期P0后续增量", "未实现"),
        Item("F21", "QLC", "P0", "一期P0后续增量", "未实现"),
        Item("F22", "认证合规", "P2", "现行P2", "未实现"),
        Item("F23", "AI", "P2", "现行P2", "未实现"),
        Item("F24", "FTL教学", "P2", "现行P2", "未实现。本设备模拟器不是 FTL 教学模拟器。"),
        Item("F25", "Handler", "P0", "一期P0后续增量", "未实现"),
        Item("F26", "集群恢复监控", "P0", "一期P0后续增量", "未实现"),
        Item("F27", "权限与审计", "P0", "一期P0后续增量", "未实现"),
        Item("F28", "首闭环结果", "P0（基础结果子集）", "首个模拟闭环", "已实现子集：任务结果、配方快照、种子、逐步记录、CSV 与 SQLite 一致。不是完整报告引擎。"),
        Item("F28", "完整指标与保留", "P0（完整单片/批次）", "一期P0后续增量", "未实现。没有 PDF、Excel、HTML 或 UBER/RBER 报告。"),
        Item("F28", "对比交互", "P1（对比交互）", "现行P1", "未实现"),
        Item("F29", "全自动闭环", "P0", "一期P0后续增量", "未实现")
    ];

    public static int ImplementedSubsetCount => Items.Count(item => item.Status.StartsWith("已实现子集", StringComparison.Ordinal));

    private static ScopeItem Item(string id, string subItem, string priority, string bucket, string status) =>
        new(id, subItem, priority, bucket, status);
}
