using System.Text;
using NandFlashTester.Api.Data;
using NandFlashTester.Core.Materials;
using NandFlashTester.Core.Patterns;
using NandFlashTester.Core.Recipes;

namespace NandFlashTester.Api.Scripts;

public sealed record ScriptDocumentRequest(string? Name, string? Body);

public sealed record RealDeviceClaim(bool SkuSpecVerified);

public static class ScriptEndpoints
{
    public static object Map() => new
    {
        hardwareExecuted = false,
        measured = false,
        items = ScriptCapabilityMap.Items
    };

    public static object Example() => new
    {
        body = ScriptExamples.PageLoop,
        notes = ScriptExamples.Notes,
        hardwareExecuted = false,
        executed = false
    };

    public static (int Status, object Body) Check(string? body, IReadOnlyDictionary<string, PatternResource>? patterns = null)
    {
        var result = ScriptCompiler.Compile(body, patterns);
        return (result.Ok ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest, new
        {
            ok = result.Ok,
            executed = false,
            hardwareExecuted = false,
            bytesEmitted = 0,
            diagnostics = Diagnostics(result),
            plan = result.Plan is null ? null : PlanView(result.Plan),
            formulaNote = PatternLibrary.FormulaNote,
            patternSnapshots = Snapshots(result.Plan, patterns)
        });
    }

    public static (int Status, object Body) Create(SqliteScriptStore store, ScriptDocumentRequest request)
    {
        var body = request.Body ?? "";
        if (Encoding.UTF8.GetByteCount(body) > ScriptCompiler.MaxBytes)
        {
            return (StatusCodes.Status400BadRequest, new { executed = false, hardwareExecuted = false, message = "脚本超过 16 KiB，未保存。" });
        }

        var compiled = ScriptCompiler.Compile(body);
        var name = compiled.Plan?.Name ?? CleanName(request.Name);
        var stored = store.Create(name, body);
        return (StatusCodes.Status201Created, new
        {
            ok = compiled.Ok,
            executed = false,
            hardwareExecuted = false,
            script = Summary(stored),
            diagnostics = Diagnostics(compiled)
        });
    }

    public static (int Status, object Body) Save(SqliteScriptStore store, string scriptId, ScriptDocumentRequest request)
    {
        var body = request.Body ?? "";
        if (Encoding.UTF8.GetByteCount(body) > ScriptCompiler.MaxBytes)
        {
            return (StatusCodes.Status400BadRequest, new { executed = false, hardwareExecuted = false, message = "脚本超过 16 KiB，未保存。" });
        }

        var compiled = ScriptCompiler.Compile(body);
        var name = compiled.Plan?.Name ?? CleanName(request.Name);
        var stored = store.Save(scriptId, name, body);
        return stored is null
            ? (StatusCodes.Status404NotFound, new { error = "not_found", message = "脚本不存在。" })
            : (StatusCodes.Status200OK, new
            {
                ok = compiled.Ok,
                executed = false,
                hardwareExecuted = false,
                script = Summary(stored),
                diagnostics = Diagnostics(compiled)
            });
    }

    public static (int Status, object Body) Plan(SqliteScriptStore store, string scriptId, IReadOnlyDictionary<string, PatternResource>? patterns = null)
    {
        var stored = store.Get(scriptId);
        if (stored is null)
        {
            return (StatusCodes.Status404NotFound, new { error = "not_found", message = "脚本不存在。" });
        }

        var compiled = ScriptCompiler.Compile(stored.Body, patterns);
        if (!compiled.Ok || compiled.Plan is null)
        {
            foreach (var diagnostic in compiled.Diagnostics)
            {
                store.AddLog(scriptId, "check", diagnostic.Line, diagnostic.Column, diagnostic.Message);
            }

            return (StatusCodes.Status400BadRequest, new
            {
                ok = false,
                executed = false,
                hardwareExecuted = false,
                bytesEmitted = 0,
                diagnostics = Diagnostics(compiled)
            });
        }

        var driver = new BlockedScriptDriver();
        var run = ScriptInterpreter.Run(compiled.Plan, driver, ScriptLimits.Default);
        if (run.HardwareExecuted || run.Measured || run.BytesEmitted != 0 || run.ErrorBitCount is not null || run.ErrorClass != "not_executed" || run.Findings.Count != 0)
        {
            throw new InvalidOperationException("配置计划产生了实测或发送。");
        }

        var linked = Snapshots(compiled.Plan, patterns);
        var messages = run.Steps.Select(step => step.Message).ToList();
        messages.AddRange(linked.Select(snapshot => $"图案 {snapshot.Id} 版本 {snapshot.Version} 摘要 {snapshot.Sha256} 页长 {snapshot.PageSize} 缓冲 {snapshot.BufferIndex}。{PatternLibrary.FormulaNote} 未发送。"));
        messages.Add($"主机计划已展开。擦除 {compiled.Plan.EraseCount}，写入 {compiled.Plan.ProgramCount}，读回 {compiled.Plan.ReadCount}，比较 {compiled.Plan.CompareCount}。错误计数未统计。硬件未执行。");
        var committed = store.CommitPlan(
            scriptId,
            stored.Sha256,
            compiled.Plan.Name,
            compiled.Plan.EraseCount,
            compiled.Plan.ProgramCount,
            compiled.Plan.ReadCount,
            compiled.Plan.CompareCount,
            string.Join(' ', linked.Select(item => item.Id).Distinct()),
            string.Join(' ', linked.Select(item => item.Sha256).Distinct()),
            messages);
        var saved = store.Get(scriptId)!;
        return (StatusCodes.Status200OK, new
        {
            ok = true,
            executed = false,
            hardwareExecuted = false,
            bytesEmitted = run.BytesEmitted,
            errorBitCount = run.ErrorBitCount,
            errorCountText = run.ErrorCountText,
            errorClass = run.ErrorClass,
            errorClassText = CompareClassifier.Text(run.ErrorClass),
            findings = run.Findings,
            measured = run.Measured,
            countsAreHostPlan = true,
            formulaNote = PatternLibrary.FormulaNote,
            patternSnapshots = linked,
            runSnapshot = new
            {
                snapshotId = committed.SnapshotId,
                status = committed.Status,
                errorClass = committed.ErrorClass,
                bytesEmitted = committed.BytesEmitted,
                hardwareRecovery = committed.HardwareRecovery,
                patternIds = committed.PatternIds,
                patternShas = committed.PatternShas,
                reason = committed.Reason
            },
            sent = false,
            script = Summary(saved),
            plan = PlanView(compiled.Plan),
            logs = Logs(store, scriptId)
        });
    }

    public static (int Status, object Body) Stop(SqliteScriptStore store, string scriptId)
    {
        if (store.Get(scriptId) is null)
        {
            return (StatusCodes.Status404NotFound, new { error = "not_found", message = "脚本不存在。" });
        }

        store.Stop(scriptId);
        var saved = store.Get(scriptId)!;
        return (StatusCodes.Status200OK, new
        {
            executed = false,
            hardwareExecuted = false,
            bytesEmitted = 0,
            script = Summary(saved),
            logs = Logs(store, scriptId)
        });
    }

    public static (int Status, object Body) Run(SqliteScriptStore store, string scriptId, bool clientClaimsSkuVerified)
    {
        var stored = store.Get(scriptId);
        if (stored is null)
        {
            return (StatusCodes.Status404NotFound, new { error = "not_found", executed = false, hardwareExecuted = false });
        }

        var decision = DeviceMaterialModel.DecideRealDevice(clientClaimsSkuVerified);
        store.AddLog(scriptId, "refused", null, null, decision.Reason);
        return (StatusCodes.Status409Conflict, new
        {
            executed = false,
            hardwareExecuted = false,
            bytesEmitted = 0,
            realDeviceAllowed = decision.Allowed,
            skuSpecVerified = decision.SkuSpecVerified,
            message = decision.Reason
        });
    }

    public static object Summary(StoredScript script) => new
    {
        scriptId = script.ScriptId,
        name = script.Name,
        body = script.Body,
        sha256 = script.Sha256,
        status = script.Status,
        eraseCount = script.EraseCount,
        programCount = script.ProgramCount,
        readCount = script.ReadCount,
        compareCount = script.CompareCount,
        hardwareExecuted = script.HardwareExecuted,
        createdAt = script.CreatedAt,
        updatedAt = script.UpdatedAt
    };

    private static IReadOnlyList<PatternSnapshot> Snapshots(ScriptPlan? plan, IReadOnlyDictionary<string, PatternResource>? patterns)
    {
        if (plan is null)
        {
            return [];
        }

        return plan.Actions
            .Where(action => action.PatternResourceId is not null)
            .Select(action =>
            {
                PatternResource? resource = null;
                patterns?.TryGetValue(action.PatternResourceId!, out resource);
                return new PatternSnapshot(action.PatternResourceId!, resource?.Version ?? "", resource?.Sha256 ?? "", resource?.PageSize ?? 0, action.PatternBufferIndex);
            })
            .Distinct()
            .ToArray();
    }

    private sealed record PatternSnapshot(string Id, string Version, string Sha256, int PageSize, int BufferIndex);

    private static object PlanView(ScriptPlan plan) => new
    {
        name = plan.Name,
        seed = plan.Seed,
        sha256 = plan.Sha256,
        eraseCount = plan.EraseCount,
        programCount = plan.ProgramCount,
        readCount = plan.ReadCount,
        compareCount = plan.CompareCount,
        hardwareExecuted = plan.HardwareExecuted,
        executionState = plan.ExecutionState,
        actions = plan.Actions.Select(action => new
        {
            index = action.Index,
            line = action.Line,
            op = action.Op,
            block = action.Block,
            page = action.Page,
            hardwareExecuted = action.HardwareExecuted,
            state = action.State
        })
    };

    private static object Diagnostics(ScriptCompileResult result) =>
        result.Diagnostics.Select(diagnostic => new { line = diagnostic.Line, column = diagnostic.Column, message = diagnostic.Message });

    private static object Logs(SqliteScriptStore store, string scriptId) =>
        store.Logs(scriptId).Select(log => new
        {
            logId = log.LogId,
            kind = log.Kind,
            line = log.Line,
            column = log.Column,
            message = log.Message,
            hardwareExecuted = log.HardwareExecuted
        });

    private static string CleanName(string? name)
    {
        var text = name?.Trim() ?? "";
        return text.Length is > 0 and <= 64 ? text : "未命名";
    }
}
