namespace NandFlashTester.Core.Scope;

public sealed record ScopeItem(string Id, string SubItem, string Priority, string Bucket, string Status);

/// <summary>
/// 19 号对照的 36 个子行。本程序只标记首个模拟闭环的子集，不把未写的 P0 从一期范围里拿掉。
/// </summary>
public static class PhaseScope
{
    public const bool SimulationOnly = false;
    public const bool ClaimsMeasuredThroughput = false;
    public const bool LocalDeliveryPathWritten = false;
    public const bool JsonEntryIsSslCompatible = false;
    public const bool HardwareAccepted = false;
    public const bool IcdProvided = false;
    public const bool WireProtocolAssumed = false;
    public const bool WindowsExecutionVerified = false;
    public const bool OldBinaryCompatibility = false;
    public const bool SslCompatibilityLayer = false;
    public const string LocalDeliveryTarget = @"D:\NandFlashTester";
    public const string ReimplementationPolicy = "旧功能全部按合法资料独立重写，并进入同一套新硬件应用。不建旧二进制、旧 DLL、旧数据格式或旧 SSL 语法兼容层，也不以 SSL 门禁阻塞新功能。禁止读取或导入反编译和旧实现。";
    public const string ExpertEntryLocation = "简单配置和自定义脚本的编辑、校验、执行、进度、结果都在同一测试页。当前自定义脚本不是完整专业脚本。";

    public static IReadOnlyList<string> ConflictsForBao { get; } =
    [
        "F07 单步调试原为 P1。若全旧能力要求调试器，与该边界冲突，请 Bao 取舍。本轮不实现，也不删除。",
        "F17 完整 16 态 Vth 原为 P1。厂商算法命令没有合法资料。请 Bao 取舍是否提前，本轮不升级、不猜测。",
        "F18 保持向导、F28 对比交互原为 P1。本轮不升级。",
        "F22、F23、F24 原为 P2。F24 不是这套硬件应用。本轮不升级。"
    ];

    public static IReadOnlyList<string> MissingLegitimateSources { get; } =
    [
        "本仓库没有旧手册正文，不能增列 19 号清单以外的旧菜单。",
        "获准的脱敏新平台协议摘录未收到。",
        "厂商特定算法命令没有合法资料。",
        "供应方 x64 驱动、签名、安装权限和 SDK 未提供。",
        "样机交付证据和 Windows 执行验收未具备。"
    ];

    public static IReadOnlyList<ScopeItem> Items { get; } =
    [
        Item("F01", "基础闭环", "P0", "新平台", "部分：主机配方步骤、取消和测试夹具比较有自动化测试。生产路径不发送。待输入：命令映射。待实机验证。"),
        Item("F02", "命令抽象", "P0", "新平台", "部分：只有逻辑意图和拒绝。待输入：线上命令映射。不能声称符合硬件。"),
        Item("F03", "图案", "P0", "新平台", "部分：increment、constant、prng 有已知答案测试。没有文件图案，也没有实机对照。"),
        Item("F04", "忙时间与应力", "P0", "新平台", "待输入：计时语义未提供。待实机验证。不填忙时间。"),
        Item("F05", "错误率ECC", "P0", "新平台", "待输入：合法 ECC 资料未提供。不实现厂商算法。"),
        Item("F06", "规模速率", "P0", "新平台", "待输入：速率语义和端点未提供。2400 MT/s 只作为已核实 WD 料号的量产约束，不是所有厂商的全局上限。待实机验证。不填实测速率。"),
        Item("F07", "受限配方", "P0（封装配方）", "新平台", "部分：同一页面可编辑配置和自定义脚本，校验带行列位置，并能展开有界循环的主机计划。计划不是硬件已执行，也不是完整专业脚本。"),
        Item("F07", "专业脚本与兼容", "P0（新脚本）", "新平台", "待输入：完整专业脚本的行为说明不足。现有自定义脚本不代替完整专业脚本。"),
        Item("F07", "单步调试", "P1（调试单步）", "冲突待Bao取舍", "未纳入本轮。原 P1。若全旧能力要求调试器，请 Bao 取舍，本轮不升、不删。"),
        Item("F08", "P/E长测恢复", "P0", "新平台", "部分：重启把未完成会话标为已中断且不续跑。待输入：部分恢复语义。待实机验证。"),
        Item("F09", "写干扰", "P0", "新平台", "待输入：合法流程说明不足。待实机验证。"),
        Item("F10", "读干扰", "P0", "新平台", "待输入：合法流程说明不足。待实机验证。"),
        Item("F11", "设备信息", "P0", "新平台", "待输入：实机身份格式未知。测试夹具里的合成标识不是本项完成。"),
        Item("F12", "基础UI", "P0（基础壳）", "新平台", "部分：同一中文页面有配置、自定义脚本、会话进度和结果。正式入口仍是未连接的实机页面。"),
        Item("F12", "完整双模式UI", "P0（产品化）", "新平台", "待输入：角色矩阵未批准。现有单页不因此删除本项。"),
        Item("F13", "结构化分布", "P0", "新平台", "待输入：分布定义未提供。页面写明未实现，不画曲线。"),
        Item("F14", "拉偏安全与降档", "P0（已定）", "新平台", "待输入：电压协议和合法限值未提供。软件只拒绝并记录，不下发。"),
        Item("F15", "校准时序", "P0（已定）", "新平台", "待输入：时序范围和算法未提供。待实机验证。"),
        Item("F16", "SKU三温及工业", "P0", "新平台", "待输入：温箱协议和 SKU 温度表未提供。三温只是未核实候选，须在 SKU 规格核实后选择，不自动当作器件许可。未经核实不允许实机。待实机验证。"),
        Item("F17", "公开命令最小集", "P0（公开命令最小集）", "新平台", "待输入：公开命令的合法行为说明未提供。不自拟载荷。"),
        Item("F17", "完整Vth", "P1（完整Vth）", "冲突待Bao取舍", "未纳入本轮。原 P1。厂商逐电平命令无合法资料。请 Bao 取舍，不猜测。"),
        Item("F18", "保持向导", "P1", "冲突待Bao取舍", "未纳入本轮。原 P1。不升级。"),
        Item("F19", "Retry工作流", "P0", "新平台", "待输入：合法档位未提供。未知档位会拒绝，没有实机流程。"),
        Item("F20", "五级与复测", "P0", "新平台", "待输入：批准阈值未提供。不产生量产分档。"),
        Item("F20", "MES接口", "P0", "新平台", "待输入：MES 名称、字段和协议未提供。"),
        Item("F21", "QLC", "P0", "新平台", "待输入：合法 QLC 资料未提供。不能用测试夹具代替验收。"),
        Item("F22", "认证合规", "P2", "未纳入本轮", "未纳入本轮。原 P2。不升级。"),
        Item("F23", "AI", "P2", "未纳入本轮", "未纳入本轮。原 P2。不升级。"),
        Item("F24", "FTL教学", "P2", "未纳入本轮", "未纳入本轮。原 P2。不是 FTL 教学模拟器，也不把设备会话当成它。"),
        Item("F25", "Handler", "P0", "新平台", "待输入：Handler 协议未提供。动作已禁用。"),
        Item("F26", "集群恢复监控", "P0", "新平台", "待输入：节点和告警渠道未提供。待实机验证。"),
        Item("F27", "权限与审计", "P0", "新平台", "待输入：角色矩阵未批准。危险操作默认禁用并记入会话，不是完整权限系统。"),
        Item("F28", "首闭环结果", "P0（基础结果子集）", "新平台", "部分：会话命令、证据级别和 CSV 可按会话编号追溯。实机通过列为否。"),
        Item("F28", "完整指标与保留", "P0（完整单片/批次）", "新平台", "待输入：完整指标没有实机结果。不生成 PDF。"),
        Item("F28", "对比交互", "P1（对比交互）", "冲突待Bao取舍", "未纳入本轮。原 P1。不升级。"),
        Item("F29", "全自动闭环", "P0", "新平台", "待输入：温箱、Handler、MES 和协议都未提供。待实机验证。")
    ];

    public static int PartialCount => Items.Count(item => item.Status.StartsWith("部分", StringComparison.Ordinal));

    private static ScopeItem Item(string id, string subItem, string priority, string bucket, string status) =>
        new(id, subItem, priority, bucket, status);
}
