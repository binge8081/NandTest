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
    public void Repeat_bounds_are_rejected_with_a_location()
    {
        var result = ScriptCompiler.Compile("REPEAT 0000021 0000001\nPGPROG 00 0000 0000 RANDOM 01\n");
        Assert.False(result.Ok);
        Assert.Contains(result.Diagnostics, item => item.Line == 1 && item.Message.Contains("1 到 32", StringComparison.Ordinal));
    }
}
