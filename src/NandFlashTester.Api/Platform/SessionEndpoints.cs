using System.Globalization;
using System.Text;
using NandFlashTester.Api.Data;
using NandFlashTester.Core.Platform;
using NandFlashTester.Core.Recipes;
using NandFlashTester.Core.Scope;

namespace NandFlashTester.Api.Platform;

public static class SessionEndpoints
{
    public const string BlockReason = "获准的新平台协议摘录未提供。未连接样机，未发送控制载荷。";

    public static object PlatformBody(bool automatedTestFixture) => new
    {
        product = "新平台 NAND 测试上位机",
        hostOs = "Windows 11 x64",
        runtimeIdentifier = "win-x64",
        mode = automatedTestFixture ? PlatformModes.AutomatedTest : PlatformModes.Production,
        deviceSource = "新平台实机路径",
        deviceConnected = false,
        oldDeviceCompatibility = false,
        sslCompatibilityLayer = false,
        icdProvided = false,
        wireProtocolAssumed = false,
        hardwareAccepted = false,
        windowsExecutionVerified = false,
        localDeliveryPathWritten = PhaseScope.LocalDeliveryPathWritten,
        localDeliveryTarget = PhaseScope.LocalDeliveryTarget,
        evidenceClass = EvidenceClasses.SoftwareContract,
        automatedTestFixtureEnabled = automatedTestFixture,
        automatedTestIsHardwarePass = false,
        gaps = IcdGapCatalog.Missing,
        dangerousOperations = LogicalIntents.Dangerous.Select(intent => new
        {
            intent,
            intentText = LogicalIntents.Text(intent),
            enabled = false,
            reason = "安全互锁协议未提供，已禁用。"
        }),
        conflictsForBao = PhaseScope.ConflictsForBao,
        missingLegitimateSources = PhaseScope.MissingLegitimateSources
    };

    public static string Open(SqliteSessionStore store)
    {
        var sessionId = "ses-" + Guid.NewGuid().ToString("N");
        store.InsertBlocked(sessionId, BlockReason);
        return sessionId;
    }

    public static async Task<(int Status, object Body)> Execute(SqliteSessionStore store, string sessionId, byte[] recipeUtf8)
    {
        var existing = store.Get(sessionId);
        if (existing is null)
        {
            return (StatusCodes.Status404NotFound, new { error = "not_found", message = "会话不存在。" });
        }

        if (existing.State is "Cancelled" or "Interrupted" or "Closed")
        {
            return (StatusCodes.Status409Conflict, new
            {
                error = "session_closed",
                message = "这个会话已经结束，没有发送。",
                executed = false,
                hardwareAccepted = false
            });
        }

        var validation = RecipeValidator.Validate(recipeUtf8);
        if (!validation.Ok || validation.Recipe is null)
        {
            return (StatusCodes.Status400BadRequest, new
            {
                error = "recipe_rejected",
                message = "配方未通过校验，未发送。",
                executed = false,
                hardwareAccepted = false,
                details = validation.Errors
            });
        }

        var transport = new BlockedHardwareTransport();
        var outcomes = await SessionEngine.RunRecipeAsync(transport, TimeSpan.FromSeconds(1), CancellationToken.None);
        if (transport.EmitCount != 0)
        {
            throw new InvalidOperationException("生产传输发出了字节。");
        }

        store.AddCommands(sessionId, outcomes);
        return (StatusCodes.Status200OK, ToDetail(store.Get(sessionId)!));
    }

    public static (int Status, object Body) Danger(SqliteSessionStore store, string sessionId, string? intent)
    {
        var existing = store.Get(sessionId);
        if (existing is null)
        {
            return (StatusCodes.Status404NotFound, new { error = "not_found", message = "会话不存在。" });
        }

        var name = string.IsNullOrWhiteSpace(intent) ? "set-voltage" : intent.Trim();
        var outcome = SessionEngine.BlockBeforeSend(name, new BlockedHardwareTransport())
            ?? new CommandOutcome(name, "Rejected", false, false, EvidenceClasses.SoftwareContract, "未知意图，未发送。", "只使用页面上的配方或已列出的禁用操作。");
        store.AddCommands(sessionId, [outcome]);
        return (StatusCodes.Status200OK, ToDetail(store.Get(sessionId)!));
    }

    public static (int Status, object Body) Cancel(SqliteSessionStore store, string sessionId)
    {
        var existing = store.Get(sessionId);
        if (existing is null)
        {
            return (StatusCodes.Status404NotFound, new { error = "not_found", message = "会话不存在。" });
        }

        store.MarkCancelled(sessionId);
        store.AddCommands(sessionId,
        [
            new CommandOutcome("cancel", "Cancelled", false, false, EvidenceClasses.SoftwareContract, "没有在途的线路传输。", "状态保持已取消。没有控制载荷需要收回。")
        ]);
        return (StatusCodes.Status200OK, ToDetail(store.Get(sessionId)!));
    }

    public static object ToDetail(StoredSession session) => new
    {
        sessionId = session.SessionId,
        state = session.State,
        stateText = StateText(session.State),
        evidenceClass = session.EvidenceClass,
        hardwareAccepted = session.HardwareAccepted,
        bytesEmitted = session.BytesEmitted,
        blockReason = session.BlockReason,
        openedAt = session.OpenedAt,
        finishedAt = session.FinishedAt,
        conclusion = session.Commands.Count == 0 ? "未执行" : session.Commands.All(command => command.State is "Rejected" or "Cancelled") ? "未执行" : "仅软件记录",
        conclusionSymbol = "–",
        eta = (string?)null,
        etaNote = "总耗时未测量，不估算剩余时间。",
        commands = session.Commands.Select(command => new
        {
            commandId = command.CommandId,
            stepIndex = command.StepIndex,
            intent = command.Intent,
            intentText = LogicalIntents.Text(command.Intent),
            state = command.State,
            stateText = StateText(command.State),
            sent = command.Sent,
            hardwareAccepted = command.HardwareAccepted,
            evidenceClass = command.EvidenceClass,
            reason = command.Reason,
            nextStep = command.NextStep
        })
    };

    public static string Csv(StoredSession session)
    {
        var builder = new StringBuilder();
        builder.Append('\uFEFF');
        builder.AppendLine("会话编号,步骤序号,意图,命令状态,已发到线路,实机通过,证据级别,原因,下一步");
        foreach (var command in session.Commands)
        {
            var cells = new[]
            {
                session.SessionId,
                command.StepIndex.ToString(CultureInfo.InvariantCulture),
                LogicalIntents.Text(command.Intent),
                StateText(command.State),
                command.Sent ? "是" : "否",
                command.HardwareAccepted ? "是" : "否",
                command.EvidenceClass,
                command.Reason,
                command.NextStep
            };
            builder.AppendLine(string.Join(',', cells.Select(Cell)));
        }

        return builder.ToString();
    }

    private static string StateText(string state) => state switch
    {
        "Blocked" => "未接入",
        "Interrupted" => "已中断",
        "Cancelled" => "已取消",
        "Closed" => "已关闭",
        "Rejected" => "未发送",
        "TimedOut" => "超时",
        "Succeeded" => "测试应答",
        "Failed" => "失败",
        _ => state
    };

    private static string Cell(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }
}
