using NandFlashTester.Core.Recipes;

namespace NandFlashTester.Tests;

public class SourceScriptTests
{
    [Fact]
    public void Repeat_and_variables_expand_into_the_same_host_plan()
    {
        var result = ScriptCompiler.Compile(
            """
            ' comment
            VSET08 00 00000001
            VADD08 00 00000002
            REPEAT 0000001 0000002
            PGPROG 00 V00 0001 RANDOM 00000001
            PGREAD 00 0000 0002
            """);
        Assert.True(result.Ok, string.Join(" ", result.Diagnostics.Select(item => item.Message)));
        Assert.Equal(2, result.Plan!.ProgramCount);
        Assert.Equal(1, result.Plan.ReadCount);
        Assert.Equal(0, result.Plan.EraseCount);
        Assert.All(result.Plan.Actions.Where(action => action.Op == "program"), action => Assert.Equal(3, action.Block));
        Assert.False(result.Plan.HardwareExecuted);
    }

    [Fact]
    public void Device_commands_and_file_paths_are_recognized_and_not_sent()
    {
        var result = ScriptCompiler.Compile(
            """
            BKERAS 00 0004
            CMND00 00 00FF 0 0
            SNDAD1 00 00 0
            SETFTR 00 01 00 00 00 00
            SETBUF /tmp/not-a-pattern
            """);
        Assert.True(result.Ok, string.Join(" ", result.Diagnostics.Select(item => item.Message)));
        Assert.Equal(1, result.Plan!.EraseCount);
        Assert.Equal(0, result.Plan.ProgramCount);
        Assert.Equal(4, result.Plan.Actions.Count(action => action.Op == "withheld"));
        var run = ScriptInterpreter.Run(result.Plan, new BlockedScriptDriver(), ScriptLimits.Default);
        Assert.Equal(0, run.BytesEmitted);
        Assert.False(run.Measured);
        Assert.Equal("not_executed", run.ErrorClass);
        Assert.Contains(run.Steps, step => step.Op == "withheld" && step.ErrorClass == "not_executed");
        Assert.Contains(run.Steps, step => step.Op == "erase" && step.Message.Contains("未发送", StringComparison.Ordinal));
    }

    [Fact]
    public void Bit_error_stop_uses_the_isolated_driver_and_does_not_continue()
    {
        var result = ScriptCompiler.Compile(
            """
            CNDSTP 0022 00000000
            PGPROG 00 0000 0000 RANDOM 00000007
            PGRDCP 00 0000 0000 0010 RANDOM 00000007
            PGPROG 00 0002 0000 RANDOM 00000007
            """);
        Assert.True(result.Ok, string.Join(" ", result.Diagnostics.Select(item => item.Message)));
        Assert.Equal(0, result.Plan!.StopWhenBitErrorsAbove);
        var run = ScriptInterpreter.Run(result.Plan, new IsolatedPageDriver((0, 0, 0, 0)), ScriptLimits.Default);
        Assert.Equal("bit_mismatch", run.ErrorClass);
        Assert.False(run.Measured);
        Assert.Equal(0, run.BytesEmitted);
        Assert.Equal(1, run.ErrorBitCount);
        Assert.Equal(1, run.Steps.Count(step => step.Op == "program"));
        Assert.Contains("停止条件", run.StopReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Byte_error_stop_fires_and_busy_time_stop_does_not()
    {
        var bytes = ScriptCompiler.Compile(
            """
            CNDSTP 0012 00000000
            PGPROG 00 0000 0000 RANDOM 00000003
            PGRDCP 00 0000 0000 0010 RANDOM 00000003
            PGPROG 00 0003 0000 RANDOM 00000003
            """);
        Assert.True(bytes.Ok, string.Join(" ", bytes.Diagnostics.Select(item => item.Message)));
        Assert.Equal("byte>", bytes.Plan!.CountStopRule);
        var stopped = ScriptInterpreter.Run(bytes.Plan, new IsolatedPageDriver((0, 0, 1, 1)), ScriptLimits.Default);
        Assert.Equal(0, stopped.BytesEmitted);
        Assert.False(stopped.Measured);
        Assert.Equal(1, stopped.Steps.Count(step => step.Op == "program"));
        Assert.Contains("停止条件", stopped.StopReason, StringComparison.Ordinal);

        var busy = ScriptCompiler.Compile(
            """
            CNDSTP 0002 0000000A
            PGPROG 00 0000 0000 ICROVL 00000003 0001
            PGRDCP 00 0000 0000 0010 RDMOVL 00000003
            PGPROG 00 0003 0000 RANDOM 00000003
            """);
        Assert.True(busy.Ok, string.Join(" ", busy.Diagnostics.Select(item => item.Message)));
        Assert.Null(busy.Plan!.CountStopRule);
        var continued = ScriptInterpreter.Run(busy.Plan, new IsolatedPageDriver((0, 0, 0, 0)), ScriptLimits.Default);
        Assert.Equal(2, continued.Steps.Count(step => step.Op == "program"));
        Assert.Equal(0, continued.BytesEmitted);
        Assert.False(continued.Measured);
    }

    [Fact]
    public void Source_command_families_plan_or_stay_unsent()
    {
        var result = ScriptCompiler.Compile(
            """
            TSTAMP
            SWRSET
            SWWAIT 00000001
            BKERAS 00 0001
            BKPROG 00 0001 ICROVL 0001 0001
            PGREAD 00 0001 0002
            BKRDCP 00 0001 0010 RANDOM 01
            CMND11 00 00FF 00 11 0 0
            SNDAD2 00 01 02 0
            SETFTR 00 01 00 00 00 00
            STATUS 00
            BUSYTM 00
            ECCANA 00
            GETDAT 00 01
            SETDT1 00 11 0
            BEMCP2 03 0001 0002
            BKERMC 01 0004
            """);
        Assert.True(result.Ok, string.Join(" ", result.Diagnostics.Select(item => item.Message)));
        var plan = result.Plan!;
        Assert.Equal(1, plan.EraseCount);
        Assert.Equal(1, plan.ProgramCount);
        Assert.True(plan.ReadCount >= 1);
        Assert.True(plan.CompareCount >= 1);
        Assert.Contains(plan.Actions, action => action.Op == "mark");
        Assert.Contains(plan.Actions, action => action.Op == "wait");
        Assert.True(plan.Actions.Count(action => action.Op == "withheld") >= 10);
        var run = ScriptInterpreter.Run(plan, new BlockedScriptDriver(), ScriptLimits.Default);
        Assert.Equal(0, run.BytesEmitted);
        Assert.False(run.HardwareExecuted);
        Assert.Equal("not_executed", run.ErrorClass);
        Assert.Contains(run.Steps, step => step.Op == "mark" && step.Message.Contains("没有发送", StringComparison.Ordinal));

        var buffer = ScriptCompiler.Compile("PGPROG 00 0000 0000 BUFATA\n");
        Assert.False(buffer.Ok);
        Assert.Contains(buffer.Diagnostics, item => item.Message.Contains("不读取外部文件", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeat_bounds_are_rejected_with_a_location()
    {
        var result = ScriptCompiler.Compile("REPEAT 0000021 0000001\nPGPROG 00 0000 0000 RANDOM 01\n");
        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, item => item.Line == 1 && item.Message.Contains("1 到 32", StringComparison.Ordinal));
    }
}
