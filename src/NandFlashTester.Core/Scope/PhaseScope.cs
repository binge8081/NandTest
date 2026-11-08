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
        Item("F01", "基础闭环", "P0", "新平台", "代码测试：主机配置、取消和夹具比较。接口替身：测试夹具。生产路径不发送。待资料：命令映射。待实机。"),
        Item("F02", "命令抽象", "P0", "新平台", "代码测试：逻辑意图和拒绝。待资料：线上命令映射。待实机。不能声称符合硬件。"),
        Item("F03", "图案", "P0", "新平台", "代码测试：increment、constant、prng，以及页面上传资源按编号、版本和摘要关联。脚本路径不读取。与旧图案逐字节等价未证。待实机。"),
        Item("F04", "忙时间与应力", "P0", "新平台", "待资料：计时语义未提供。待实机。不填忙时间。"),
        Item("F05", "错误率ECC", "P0", "新平台", "代码测试：合法分母计算原始误码率，单位是错误位/已比较位。来源字符串不能单独成为实测。无 ECC 定义，UBER 未统计，不画曲线，不做未知阈值判定。待实机。"),
        Item("F06", "规模速率", "P0", "新平台", "代码测试：2400 MT/s 只作为已核实 WD 料号的量产约束。待资料：速率语义和端点。待实机。不填实测速率。"),
        Item("F07", "受限配置", "P0（封装配置）", "新平台", "代码测试：同一页面编辑配置和自定义脚本，校验带行列，展开有界循环并保存版本快照。计划不是硬件已执行，也不是完整专业脚本。"),
        Item("F07", "专业脚本与兼容", "P0（新脚本）", "新平台", "待资料：完整专业脚本的行为说明不足。现有自定义脚本不代替完整专业脚本。"),
        Item("F07", "单步调试", "P1（调试单步）", "冲突待Bao取舍", "未纳入本轮。原 P1。若全旧能力要求调试器，请 Bao 取舍，本轮不升、不删。"),
        Item("F08", "P/E长测恢复", "P0", "新平台", "代码测试：主机重启把未提交的发送前快照标为发送前中断，发送字节保持 0。这不是硬件断点续传。待资料：在途传输协议。待实机。"),
        Item("F09", "写干扰", "P0", "新平台", "待资料：合法流程说明不足。待实机。"),
        Item("F10", "读干扰", "P0", "新平台", "待资料：合法流程说明不足。待实机。"),
        Item("F11", "设备信息", "P0", "新平台", "待资料：实机身份格式未知。接口替身里的合成标识不是本项完成。待实机。"),
        Item("F12", "基础UI", "P0（基础壳）", "新平台", "代码测试：同一中文页面有配置、自定义脚本、快照和未连接入口。Windows 启动脚本有端口占用和单实例拒绝的代码对照，未在用户电脑执行。Windows 执行验收仍为否。"),
        Item("F12", "完整双模式UI", "P0（产品化）", "新平台", "待资料：角色矩阵未批准。拒绝逻辑存在，但不称已批准。现有单页不因此删除本项。"),
        Item("F13", "结构化分布", "P0", "新平台", "待资料：分布定义未提供。页面不画曲线。"),
        Item("F14", "拉偏安全与降档", "P0（已定）", "新平台", "待资料：电压协议和合法限值未提供。代码测试：软件拒绝并记录，不下发。待输入。"),
        Item("F15", "校准时序", "P0（已定）", "新平台", "待资料：时序范围和算法未提供。待实机。"),
        Item("F16", "SKU三温及工业", "P0", "新平台", "待资料：温箱协议和 SKU 温度表未提供。代码测试：三温只是未核实候选，不自动当作器件许可，未经核实不允许实机。待输入。待实机。"),
        Item("F17", "公开命令最小集", "P0（公开命令最小集）", "新平台", "待资料：公开命令的合法行为说明未提供。不自拟载荷。待实机。"),
        Item("F17", "完整Vth", "P1（完整Vth）", "冲突待Bao取舍", "未纳入本轮。原 P1。厂商逐电平命令无合法资料。请 Bao 取舍，不猜测。"),
        Item("F18", "保持向导", "P1", "冲突待Bao取舍", "未纳入本轮。原 P1。不升级。"),
        Item("F19", "Retry工作流", "P0", "新平台", "待资料：合法档位未提供。代码测试：未知档位拒绝。没有实机流程。"),
        Item("F20", "五级与复测", "P0", "新平台", "待资料：批准阈值未提供。代码测试：未批准阈值一律拒绝，不产生量产分档，也不称已批准。"),
        Item("F20", "MES接口", "P0", "新平台", "待资料：MES 名称、字段和协议未提供。代码测试：未批准项拒绝，不称已对接。"),
        Item("F21", "QLC", "P0", "新平台", "待资料：合法 QLC 资料未提供。接口替身不能代替验收。待实机。"),
        Item("F22", "认证合规", "P2", "未纳入本轮", "未纳入本轮。原 P2。不升级。"),
        Item("F23", "AI", "P2", "未纳入本轮", "未纳入本轮。原 P2。不升级。"),
        Item("F24", "FTL教学", "P2", "未纳入本轮", "未纳入本轮。原 P2。不是 FTL 教学模拟器，也不把设备会话当成它。"),
        Item("F25", "Handler", "P0", "新平台", "待资料：Handler 协议未提供。代码测试：动作已禁用。待实机。"),
        Item("F26", "集群恢复监控", "P0", "新平台", "待资料：节点和告警渠道未提供。待实机。"),
        Item("F27", "权限与审计", "P0", "新平台", "代码测试：参数导入留下提议值且保持未启用。角色、MES 和五级阈值未批准，拒绝后不放行。"),
        Item("F28", "首闭环结果", "P0（基础结果子集）", "新平台", "代码测试：运行快照错误分类为未执行，发送字节为 0，导出含脚本摘要和图案摘要。接口替身可记录位不一致位置，但不作为实测。待实机。"),
        Item("F28", "完整指标与保留", "P0（完整单片/批次）", "新平台", "待资料：完整指标没有实机结果。不生成 PDF。待实机。"),
        Item("F28", "对比交互", "P1（对比交互）", "冲突待Bao取舍", "未纳入本轮。原 P1。不升级。"),
        Item("F29", "全自动闭环", "P0", "新平台", "待资料：温箱、Handler、MES 和协议都未提供。待输入。待实机。")
    ];

    public static int PartialCount => Items.Count(item => item.Status.Contains("代码测试", StringComparison.Ordinal));

    private static ScopeItem Item(string id, string subItem, string priority, string bucket, string status) =>
        new(id, subItem, priority, bucket, status);
}
