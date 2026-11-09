namespace NandFlashTester.Core.Recipes;

public sealed record ScriptCapability(string Name, string Status, string Evidence, string Gap);

public static class ScriptCapabilityMap
{
    public static IReadOnlyList<ScriptCapability> Items { get; } =
    [
        new("源脚本命令文本", "主机已实现", "SourceScript；SourceScriptTests。REPEAT、变量运算、BKERAS、PGPROG、PGRDCP 进入同一主机计划", "CMND、SNDAD、SETFTR、STATUS、BUSYTM、ECCANA 和文件路径只识别不发送；图案字节不声称与外部执行器逐字节一致"),
        new("新建、编辑、保存、打开", "主机已实现", "SqliteScriptStore；ScriptCompilerTests.Saved_script_plans_stops_and_exports_without_sending", "无"),
        new("语法与语义校验定位", "主机已实现", "ScriptCompiler；ScriptCompilerTests.Errors_carry_line_and_column_and_reject_unbounded_or_unreadable_compare", "无"),
        new("参数变量、有界循环、分支", "主机已实现", "循环 1 到 32，嵌套最多 2 层；ScriptCompilerTests.If_keeps_only_the_taken_branch", "无"),
        new("擦除、写入、读回、比较计数", "主机已实现", "ScriptPlan 计数；ScriptCompilerTests.Example_expands_bounded_counts_without_hardware_execution", "计数来自主机计划，不是实测"),
        new("错误计数与分类", "待实机", "CompareClassifier；Isolated_driver_counts_bits_without_claiming_a_measurement 只分类隔离介质", "正式路径分类为未执行，计数未统计，主机计划不是已完成的擦写读"),
        new("进度、停止、导出追溯", "主机已实现", "script_logs、run_snapshots 与导出 CSV；RunSnapshotTests", "停止和主机重启都发生在发送之前，不是硬件断点续传"),
        new("硬件操作", "不可执行", "BlockedScriptDriver 调用 SessionEngine.BlockBeforeSend", "待协议摘录和样机"),
        new("单步调试", "未实现", "PhaseScope F07 单步调试", "原 P1，缺资料，待 Bao 取舍，不猜测"),
        new("文件图案", "主机已实现", "PatternLibrary 与页面上传。SETBUF 只接受资源编号", "不读脚本路径。INCRMT 等与旧逐字节等价未证"),
        new("厂商命令与线上载荷", "未实现", "不生成操作码或控制载荷", "缺合法命令说明，不猜测语义"),
        new("完整专业脚本", "未实现", "现有语言不代替完整专业脚本", "行为说明不足")
    ];
}
