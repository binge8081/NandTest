namespace NandFlashTester.Core.Recipes;

public sealed record ScriptCapability(string Name, string Status, string Evidence, string Gap);

public static class ScriptCapabilityMap
{
    public static IReadOnlyList<ScriptCapability> Items { get; } =
    [
        new("新建、编辑、保存、打开", "主机已实现", "SqliteScriptStore；ScriptCompilerTests.Saved_script_plans_stops_and_exports_without_sending", "无"),
        new("语法与语义校验定位", "主机已实现", "ScriptCompiler；ScriptCompilerTests.Errors_carry_line_and_column_and_reject_unbounded_or_unreadable_compare", "无"),
        new("参数变量、有界循环、分支", "主机已实现", "循环 1 到 32，嵌套最多 2 层；ScriptCompilerTests.If_keeps_only_the_taken_branch", "无"),
        new("擦除、写入、读回、比较计数", "主机已实现", "ScriptPlan 计数；ScriptCompilerTests.Example_expands_bounded_counts_without_hardware_execution", "计数来自主机计划，不是实测"),
        new("错误计数", "待实机", "ScriptInterpreter 与 IsolatedPageDriver 只在自动化测试里对隔离介质计数", "产品路径写未统计，不把隔离计数当成实测成功"),
        new("进度、停止、导出追溯", "主机已实现", "script_logs 与导出 CSV", "停止发生在发送之前"),
        new("硬件操作", "不可执行", "BlockedScriptDriver 调用 SessionEngine.BlockBeforeSend", "待协议摘录和样机"),
        new("单步调试", "未实现", "PhaseScope F07 单步调试", "原 P1，缺资料，待 Bao 取舍，不猜测"),
        new("文件图案", "未实现", "当前语言没有文件图案", "缺资料"),
        new("厂商命令与线上载荷", "未实现", "不生成操作码或控制载荷", "缺合法命令说明，不猜测语义"),
        new("完整专业脚本", "未实现", "现有语言不代替完整专业脚本", "行为说明不足")
    ];
}
