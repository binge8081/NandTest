using System.Net.Http.Json;
using System.Text.Json;
using NandFlashTester.Core.Platform;
using NandFlashTester.Core.Recipes;
using NandFlashTester.Tests.Support;

namespace NandFlashTester.Tests;

public class ScriptCompilerTests
{
    [Fact]
    public void Example_expands_bounded_counts_without_hardware_execution()
    {
        var result = ScriptCompiler.Compile(ScriptExamples.PageLoop);
        Assert.True(result.Ok);
        var plan = result.Plan!;
        Assert.Equal("页编程比较", plan.Name);
        Assert.Equal(1, plan.EraseCount);
        Assert.Equal(2, plan.ProgramCount);
        Assert.Equal(2, plan.ReadCount);
        Assert.Equal(2, plan.CompareCount);
        Assert.Equal(7, plan.Actions.Count);
        Assert.False(plan.HardwareExecuted);
        Assert.Equal("planned", plan.ExecutionState);
        Assert.All(plan.Actions, action => Assert.False(action.HardwareExecuted));
        Assert.Equal([0, 1], plan.Actions.Where(action => action.Op == "program").Select(action => action.Page));
    }

    [Fact]
    public void If_keeps_only_the_taken_branch()
    {
        var result = ScriptCompiler.Compile(
            """
            name 分支
            seed 3
            var pages = 1
            if pages == 0
              erase 0
            else
              erase 1
              read 1 0
              compare 1 0
            end
            """);
        Assert.True(result.Ok, string.Join(" ", result.Diagnostics.Select(item => item.Message)));
        Assert.Equal(1, result.Plan!.EraseCount);
        Assert.Equal(1, result.Plan.Actions.Single(action => action.Op == "erase").Block);
        Assert.Equal(0, result.Plan.ProgramCount);
        Assert.Equal(1, result.Plan.CompareCount);
        Assert.False(result.Plan.HardwareExecuted);
    }

    [Fact]
    public void Errors_carry_line_and_column_and_reject_unbounded_or_unreadable_compare()
    {
        var loop = ScriptCompiler.Compile("name 过多\nseed 1\nloop 33\n  erase 0\nend\n");
        Assert.False(loop.Ok);
        Assert.Contains(loop.Diagnostics, item => item.Line == 3 && item.Message.Contains("1 到 32", StringComparison.Ordinal));

        var deep = ScriptCompiler.Compile("name 过深\nseed 1\nloop 1\n  loop 1\n    loop 1\n      erase 0\n    end\n  end\nend\n");
        Assert.False(deep.Ok);
        Assert.Contains(deep.Diagnostics, item => item.Line == 5 && item.Message.Contains("嵌套", StringComparison.Ordinal));

        var compare = ScriptCompiler.Compile("name 比较\nseed 1\ncompare 0 0\n");
        Assert.False(compare.Ok);
        Assert.Equal(3, compare.Diagnostics[0].Line);
        Assert.Equal(1, compare.Diagnostics[0].Column);
        Assert.Contains("没有同页读回", compare.Diagnostics[0].Message, StringComparison.Ordinal);

        var forbidden = ScriptCompiler.Compile("name 拒绝\nseed 1\n  exec 0\n");
        Assert.False(forbidden.Ok);
        Assert.Equal(3, forbidden.Diagnostics[0].Line);
        Assert.Equal(3, forbidden.Diagnostics[0].Column);
    }

    [Fact]
    public async Task Saved_script_plans_stops_and_exports_without_sending()
    {
        var database = TestApp.NewDatabasePath();
        string scriptId;
        await using (var app = new TestApp(database, platformMode: "Production"))
        {
            using var client = app.CreateClient();
            var coverage = await client.PostAsJsonAsync("/api/materials/coverage", new
            {
                requestedMtPerS = 3200,
                vendor = "WD",
                skuClass = "industrial",
                temperatures = new[] { -40, 25, 85 },
                skuSpecVerified = true
            });
            Assert.Equal(200, (int)coverage.StatusCode);
            using var coverageBody = JsonDocument.Parse(await coverage.Content.ReadAsStringAsync());
            Assert.False(coverageBody.RootElement.GetProperty("realDeviceAllowed").GetBoolean());
            Assert.False(coverageBody.RootElement.GetProperty("skuSpecVerified").GetBoolean());
            Assert.False(coverageBody.RootElement.GetProperty("rate").GetProperty("capApplies").GetBoolean());
            Assert.Equal(JsonValueKind.Null, coverageBody.RootElement.GetProperty("rate").GetProperty("massProductionCapMtPerS").ValueKind);
            Assert.True(coverageBody.RootElement.GetProperty("temperature").GetProperty("withinCandidate").GetBoolean());
            Assert.False(coverageBody.RootElement.GetProperty("temperature").GetProperty("devicePermissionGranted").GetBoolean());

            var created = await client.PostAsJsonAsync("/api/scripts", new { body = ScriptExamples.PageLoop });
            Assert.Equal(201, (int)created.StatusCode);
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            scriptId = createdBody.RootElement.GetProperty("script").GetProperty("scriptId").GetString()!;
            Assert.False(createdBody.RootElement.GetProperty("hardwareExecuted").GetBoolean());

            var planned = await client.PostAsync($"/api/scripts/{scriptId}/plan", new StringContent(""));
            Assert.Equal(200, (int)planned.StatusCode);
            using var planBody = JsonDocument.Parse(await planned.Content.ReadAsStringAsync());
            Assert.False(planBody.RootElement.GetProperty("executed").GetBoolean());
            Assert.False(planBody.RootElement.GetProperty("hardwareExecuted").GetBoolean());
            Assert.Equal(0, planBody.RootElement.GetProperty("bytesEmitted").GetInt32());
            Assert.False(planBody.RootElement.GetProperty("sent").GetBoolean());
            Assert.Equal(1, planBody.RootElement.GetProperty("plan").GetProperty("eraseCount").GetInt32());
            Assert.Equal(2, planBody.RootElement.GetProperty("plan").GetProperty("programCount").GetInt32());
            Assert.Equal(2, planBody.RootElement.GetProperty("plan").GetProperty("readCount").GetInt32());
            Assert.Equal(2, planBody.RootElement.GetProperty("plan").GetProperty("compareCount").GetInt32());
            Assert.Contains(planBody.RootElement.GetProperty("logs").EnumerateArray(), item =>
                item.GetProperty("kind").GetString() == "engine"
                && item.GetProperty("hardwareExecuted").GetBoolean() == false
                && item.GetProperty("message").GetString()!.Contains("未发送", StringComparison.Ordinal));

            var run = await client.PostAsJsonAsync($"/api/scripts/{scriptId}/run", new { skuSpecVerified = true });
            Assert.Equal(409, (int)run.StatusCode);
            using var runBody = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
            Assert.False(runBody.RootElement.GetProperty("executed").GetBoolean());
            Assert.False(runBody.RootElement.GetProperty("realDeviceAllowed").GetBoolean());
            Assert.False(runBody.RootElement.GetProperty("skuSpecVerified").GetBoolean());
            Assert.Equal(0, runBody.RootElement.GetProperty("bytesEmitted").GetInt32());

            var stopped = await client.PostAsync($"/api/scripts/{scriptId}/stop", new StringContent(""));
            using var stopBody = JsonDocument.Parse(await stopped.Content.ReadAsStringAsync());
            Assert.Equal("stopped_before_send", stopBody.RootElement.GetProperty("script").GetProperty("status").GetString());
            Assert.Equal(0, stopBody.RootElement.GetProperty("bytesEmitted").GetInt32());
            Assert.False(stopBody.RootElement.GetProperty("hardwareExecuted").GetBoolean());

            var csv = await client.GetStringAsync($"/api/scripts/{scriptId}/export.csv");
            Assert.Contains("领存NFA1000闪存测试仪", csv, StringComparison.Ordinal);
            Assert.Contains("硬件未执行", csv, StringComparison.Ordinal);
            Assert.Contains("未统计", csv, StringComparison.Ordinal);
            Assert.Contains("未执行", csv, StringComparison.Ordinal);
            Assert.Contains("不是已完成的擦写读", csv, StringComparison.Ordinal);
            Assert.Equal("未统计", planBody.RootElement.GetProperty("errorCountText").GetString());
            Assert.Equal(JsonValueKind.Null, planBody.RootElement.GetProperty("errorBitCount").ValueKind);
            Assert.Equal("not_executed", planBody.RootElement.GetProperty("errorClass").GetString());
            Assert.Equal("未执行", planBody.RootElement.GetProperty("errorClassText").GetString());
            Assert.Equal(0, planBody.RootElement.GetProperty("findings").GetArrayLength());
            Assert.True(planBody.RootElement.GetProperty("countsAreHostPlan").GetBoolean());
            Assert.False(planBody.RootElement.GetProperty("measured").GetBoolean());

            using var map = JsonDocument.Parse(await (await client.GetAsync("/api/scripts/map")).Content.ReadAsStringAsync());
            var items = map.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Contains(items, item => item.GetProperty("name").GetString()!.Contains("错误计数", StringComparison.Ordinal) && item.GetProperty("status").GetString() == "待实机");
            Assert.Contains(items, item => item.GetProperty("name").GetString()!.Contains("厂商命令", StringComparison.Ordinal) && item.GetProperty("status").GetString() == "未实现");
            Assert.DoesNotContain(items, item => item.GetProperty("status").GetString()!.Contains("已验收", StringComparison.Ordinal));
            Assert.Contains("stopped_before_send", csv, StringComparison.Ordinal);
        }

        await using (var app = new TestApp(database, platformMode: "Production"))
        {
            using var client = app.CreateClient();
            var opened = await client.GetAsync($"/api/scripts/{scriptId}");
            using var body = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
            Assert.Equal(ScriptExamples.PageLoop, body.RootElement.GetProperty("script").GetProperty("body").GetString());
            Assert.False(body.RootElement.GetProperty("script").GetProperty("hardwareExecuted").GetBoolean());
            Assert.Contains(body.RootElement.GetProperty("logs").EnumerateArray(), item => item.GetProperty("kind").GetString() == "stop");
        }
    }

    [Fact]
    public void Isolated_driver_counts_bits_without_claiming_a_measurement()
    {
        var plan = ScriptCompiler.Compile(ScriptExamples.PageLoop).Plan!;
        var clean = ScriptInterpreter.Run(plan, new IsolatedPageDriver(), ScriptLimits.Default);
        Assert.False(clean.Measured);
        Assert.False(clean.HardwareExecuted);
        Assert.Equal(0, clean.BytesEmitted);
        Assert.Equal(0, clean.ErrorBitCount);
        Assert.Equal("match", clean.ErrorClass);
        Assert.Empty(clean.Findings);
        Assert.Equal(EvidenceClasses.AutomatedTest, clean.EvidenceClass);

        var flipped = ScriptInterpreter.Run(plan, new IsolatedPageDriver((0, 0, 0, 0)), ScriptLimits.Default);
        Assert.Equal(1, flipped.ErrorBitCount);
        Assert.Equal("bit_mismatch", flipped.ErrorClass);
        Assert.False(flipped.Measured);
        Assert.Equal(0, flipped.BytesEmitted);
        var finding = Assert.Single(flipped.Findings);
        Assert.Equal(0, finding.Page);
        Assert.Equal(0, finding.ByteOffset);
        Assert.Equal(0, finding.Bit);
        Assert.Contains(flipped.Steps, step => step.Op == "compare" && step.ErrorClass == "bit_mismatch" && step.Message.Contains("不是实测", StringComparison.Ordinal));
    }

    [Fact]
    public void Blocked_driver_leaves_error_counts_unset_and_emits_nothing()
    {
        var plan = ScriptCompiler.Compile(ScriptExamples.PageLoop).Plan!;
        var run = ScriptInterpreter.Run(plan, new BlockedScriptDriver(), ScriptLimits.Default);
        Assert.Null(run.ErrorBitCount);
        Assert.Equal("未统计", run.ErrorCountText);
        Assert.Equal("not_executed", run.ErrorClass);
        Assert.Empty(run.Findings);
        Assert.Equal(0, run.BytesEmitted);
        Assert.False(run.Measured);
        Assert.Equal(7, run.Steps.Count);
        Assert.All(run.Steps, step =>
        {
            Assert.Equal("不可执行", step.State);
            Assert.Equal("not_executed", step.ErrorClass);
            Assert.False(step.HardwareExecuted);
        });
        Assert.Contains(run.Steps, step => step.Message.Contains("未发送", StringComparison.Ordinal));
    }

    [Fact]
    public void Interpreter_stops_on_step_time_and_memory_limits_and_rejects_host_commands()
    {
        var plan = ScriptCompiler.Compile(ScriptExamples.PageLoop).Plan!;
        var stepped = ScriptInterpreter.Run(plan, new IsolatedPageDriver(), new ScriptLimits(1, 1024, TimeSpan.FromSeconds(2)));
        Assert.Single(stepped.Steps);
        Assert.Contains("步数达到上限", stepped.StopReason, StringComparison.Ordinal);

        var timed = ScriptInterpreter.Run(plan, new IsolatedPageDriver(), ScriptLimits.Default, () => TimeSpan.FromSeconds(3));
        Assert.Empty(timed.Steps);
        Assert.Contains("时间上限", timed.StopReason, StringComparison.Ordinal);

        var actions = Enumerable.Range(0, 65).Select(index => new ScriptAction(index, 1, "program", 0, index, false, "planned")).ToArray();
        var wide = new ScriptPlan("宽", 1, "AA", 0, 65, 0, 0, false, "planned", actions);
        var memory = ScriptInterpreter.Run(wide, new IsolatedPageDriver(), new ScriptLimits(80, 64 * IsolatedPageDriver.PageSize, TimeSpan.FromSeconds(2)));
        Assert.Contains("页数超过", memory.StopReason, StringComparison.Ordinal);
        Assert.False(memory.Measured);

        var command = new ScriptPlan("命令", 1, "AA", 0, 0, 0, 0, false, "planned", [new ScriptAction(0, 1, "exec", 0, null, false, "planned")]);
        var rejected = ScriptInterpreter.Run(command, new IsolatedPageDriver(), ScriptLimits.Default);
        Assert.Contains("不能执行", rejected.StopReason, StringComparison.Ordinal);
        Assert.Equal(0, rejected.BytesEmitted);
    }
}
