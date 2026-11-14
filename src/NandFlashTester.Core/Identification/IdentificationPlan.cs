namespace NandFlashTester.Core.Identification;

public sealed record LogicalCycle(string Kind, string? OpcodeHex, string? AddressHex, int? WaitNs, string? WaitSource, int? ReadLength, string Note);

public sealed record PlanStep(string TargetId, string TargetLabel, string Operation, bool Included, bool Blocked, string? BlockReason, IReadOnlyList<LogicalCycle> Cycles);

public sealed record IdentificationPlan(string? CardId, IReadOnlyList<PlanStep> Steps, bool AnyBlocked, string Note);

public static class IdentificationPlanBuilder
{
    public static IdentificationPlan Build(SkuCard? card, IReadOnlyList<DutTarget> targets)
    {
        if (targets.Count == 0)
        {
            return new IdentificationPlan(card?.CardId, [], true, "没有选中的 LUN。DUT_MAP 为空时不能发送。");
        }

        if (card is null)
        {
            var blocked = targets.Select(target => new PlanStep(target.Id, target.Label, "read-id", false, true, "没有指定参数卡。时序和命令长度都未给出，不发送。", [])).ToList();
            return new IdentificationPlan(null, blocked, true, "未指定参数卡，命令计划只记录拒绝原因。");
        }

        var phaseBlock = (string?)null;
        var phase = ChoosePhase(card, out phaseBlock);
        var steps = new List<PlanStep>();
        foreach (var target in targets)
        {
            if (phaseBlock is not null || phase is null)
            {
                steps.Add(new PlanStep(target.Id, target.Label, "timing", false, true, phaseBlock ?? "参数卡没有时序阶段。", []));
                continue;
            }

            steps.Add(Reset(card, target, phase));
            if (card.ResetRequiredFirst is null)
            {
                steps.Add(new PlanStep(target.Id, target.Label, "read-id", false, true, "上电后是否必须先发 FFh 未在参数卡给出，不能决定读 ID 是否可以发送。", []));
                continue;
            }

            foreach (var address in new[] { "00", "20", "40" })
            {
                steps.Add(ReadId(card, target, phase, address));
            }

            steps.Add(Unique(card, target, phase));
            steps.Add(Parameter(card, target, phase));
        }

        var included = steps.Where(step => step.Included).ToList();
        return new IdentificationPlan(card.CardId, steps, steps.Any(step => step.Blocked), included.Count == 0 ? "没有可以发送的步骤。" : "这是逻辑周期，不是线上帧。");
    }

    private static TimingPhase? ChoosePhase(SkuCard card, out string? block)
    {
        block = null;
        var post = card.Phase("post-init");
        var pre = card.Phase("pre-init");
        var fallback = card.Phase("default");
        if (card.ResetRequiredFirst == true)
        {
            return post ?? fallback;
        }

        if (card.ResetRequiredFirst == false)
        {
            return fallback ?? post;
        }

        if (pre is not null && post is not null)
        {
            block = "初始化阶段未知。参数卡同时给出 pre-init 与 post-init，不在两者之间猜测。";
            return null;
        }

        return fallback ?? post ?? pre;
    }

    private static PlanStep Reset(SkuCard card, DutTarget target, TimingPhase phase)
    {
        if (card.ResetRequiredFirst == false)
        {
            return new PlanStep(target.Id, target.Label, "reset", false, false, "参数卡标明读 ID 前不要求 FFh。", []);
        }

        if (card.ResetRequiredFirst is null)
        {
            return new PlanStep(target.Id, target.Label, "reset", false, true, "是否必须先发 FFh 待补，不发送复位。", []);
        }

        if (card.ResetOpcodes.Count == 0)
        {
            return new PlanStep(target.Id, target.Label, "reset", false, true, "复位命令字节待补，不发送。", []);
        }

        var capMs = phase.TPorMaxMs ?? card.ResetPorMaxMs;
        if (capMs is null)
        {
            return new PlanStep(target.Id, target.Label, "reset", false, true, "tPOR / tRST 待补，不发送复位，也不用软件超时冒充规格。", []);
        }

        var cycles = new List<LogicalCycle>
        {
            Command(card.ResetOpcodes[0], "复位"),
            new("status", "70", null, null, null, 1, $"轮询状态直到 bit6=1，上限 {capMs} ms，来源是参数卡 tPOR max。bit0=1 则本 LUN 停止。")
        };
        return new PlanStep(target.Id, target.Label, "reset", true, false, null, cycles);
    }

    private static PlanStep ReadId(SkuCard card, DutTarget target, TimingPhase phase, string address)
    {
        var spec = card.Address(address);
        if (spec?.Supported == false)
        {
            return new PlanStep(target.Id, target.Label, "read-id-" + address, false, false, $"参数卡标明不支持 90h 地址 {address}h。", []);
        }

        if (spec?.Supported is null)
        {
            return new PlanStep(target.Id, target.Label, "read-id-" + address, false, true, $"90h 地址 {address}h 是否支持待补，不发送。", []);
        }

        if (spec.LengthBytes is null || spec.LengthUncertain)
        {
            return new PlanStep(target.Id, target.Label, "read-id-" + address, false, true, $"90h 地址 {address}h 的字节数待补，不发送。", []);
        }

        if (phase.TWhrMinNs is null)
        {
            return new PlanStep(target.Id, target.Label, "read-id-" + address, false, true, "tWHR 待补，不发送 Read ID。", []);
        }

        var cycles = new List<LogicalCycle>
        {
            Command(0x90, "Read ID"),
            new("address", null, address, null, null, null, "一个地址周期"),
            Wait(phase.TWhrMinNs.Value, $"参数卡 {phase.Name} tWHR"),
            DataOut(spec.LengthBytes.Value, "Read ID 数据")
        };
        cycles.Add(TimingNote(phase));
        return new PlanStep(target.Id, target.Label, "read-id-" + address, true, false, null, cycles);
    }

    private static PlanStep Unique(SkuCard card, DutTarget target, TimingPhase phase)
    {
        if (card.UniqueIdSupported == false)
        {
            return new PlanStep(target.Id, target.Label, "unique-id", false, false, "参数卡标明没有 Read Unique ID。", []);
        }

        if (card.UniqueIdSupported is null)
        {
            return new PlanStep(target.Id, target.Label, "unique-id", false, true, "是否支持 Unique ID 待补，不发送。", []);
        }

        var opcode = card.UniqueIdOpcode ?? (card.UniqueIdLayout == "onfi-public" ? (byte)0xED : (byte?)null);
        var copies = card.UniqueIdCopies ?? (card.UniqueIdLayout == "onfi-public" ? 16 : (int?)null);
        var copyBytes = card.UniqueIdCopyBytes ?? (card.UniqueIdLayout == "onfi-public" ? 32 : (int?)null);
        if (opcode is null || copies is null || copyBytes is null)
        {
            return new PlanStep(target.Id, target.Label, "unique-id", false, true, "Unique ID 的命令或份数待补，不套用未声明的默认值。", []);
        }

        if (!TryReadySequence(card, phase, "Unique ID", out var ready, out var reason))
        {
            return new PlanStep(target.Id, target.Label, "unique-id", false, true, reason, []);
        }

        var cycles = new List<LogicalCycle> { Command(opcode.Value, "Read Unique ID"), new("address", null, "00", null, null, null, "地址 00h") };
        cycles.AddRange(ready);
        cycles.Add(DataOut(copies.Value * copyBytes.Value, card.UniqueIdLayout == "onfi-public" ? OnfiPublic.UniqueIdCitation : "参数卡给出的份数"));
        return new PlanStep(target.Id, target.Label, "unique-id", true, false, null, cycles);
    }

    private static PlanStep Parameter(SkuCard card, DutTarget target, TimingPhase phase)
    {
        if (card.ParameterPageSupported == false)
        {
            return new PlanStep(target.Id, target.Label, "parameter-page", false, false, "参数卡标明没有 Read Parameter Page。", []);
        }

        if (card.ParameterPageSupported is null)
        {
            return new PlanStep(target.Id, target.Label, "parameter-page", false, true, "是否支持参数页待补，不发送。", []);
        }

        var copyLength = card.CopyLength ?? (card.ParameterPageLayout == "onfi-public" ? OnfiPublic.CopyLength : (int?)null);
        var copies = card.MinCopies ?? (card.ParameterPageLayout == "onfi-public" ? OnfiPublic.MinimumCopies : (int?)null);
        if (copyLength is null || copies is null)
        {
            return new PlanStep(target.Id, target.Label, "parameter-page", false, true, "参数页份数或每份长度待补，不发送。", []);
        }

        var addresses = card.ParameterPageAddresses.Count > 0
            ? card.ParameterPageAddresses
            : card.ParameterPageLayout == "onfi-public" ? new byte[] { 0x00 } : [];
        if (addresses.Count == 0)
        {
            return new PlanStep(target.Id, target.Label, "parameter-page", false, true, "参数页地址待补，不发送。", []);
        }

        if (!TryReadySequence(card, phase, "参数页", out var ready, out var reason))
        {
            return new PlanStep(target.Id, target.Label, "parameter-page", false, true, reason, []);
        }

        var cycles = new List<LogicalCycle>();
        foreach (var address in addresses)
        {
            cycles.Add(Command(card.ParameterPageOpcode, "Read Parameter Page"));
            cycles.Add(new LogicalCycle("address", null, address.ToString("X2"), null, null, null, "参数页地址"));
            cycles.AddRange(ready);
            var lengthNote = card.ParameterPageLayout == "onfi-public" && card.MinCopies is null ? OnfiPublic.CopyCitation : "参数卡给出的份数";
            cycles.Add(DataOut(copies.Value * copyLength.Value, lengthNote));
        }

        if (!string.IsNullOrWhiteSpace(card.ParameterPageBusyNote))
        {
            cycles.Add(new LogicalCycle("note", null, null, null, null, null, card.ParameterPageBusyNote));
        }

        return new PlanStep(target.Id, target.Label, "parameter-page", true, false, null, cycles);
    }

    private static bool TryReadySequence(SkuCard card, TimingPhase phase, string name, out List<LogicalCycle> cycles, out string? reason)
    {
        cycles = [];
        reason = null;
        if (phase.TRrMinNs is null)
        {
            reason = $"tRR 待补，不发送 {name}。";
            return false;
        }

        if (card.StatusProcedure is "poll-70h-then-00h" or "poll-70h-then-05h-e0h")
        {
            if (card.StatusPollCapUs is null || phase.TWhrMinNs is null)
            {
                reason = $"{name} 的状态轮询上限或 tWHR 待补，不发送。";
                return false;
            }

            cycles.Add(new LogicalCycle("status", "70", null, null, null, 1, $"轮询到 bit6=1，上限 {card.StatusPollCapUs} µs，来源是参数卡，不是阵列 tR。"));
            cycles.Add(Wait(phase.TWhrMinNs.Value, $"参数卡 {phase.Name} tWHR"));
            if (card.StatusProcedure == "poll-70h-then-00h")
            {
                cycles.Add(Command(0x00, "重新进入数据输出"));
            }
            else
            {
                cycles.Add(Command(0x05, "改变列地址"));
                cycles.Add(new LogicalCycle("address", null, "00", null, null, null, "列地址由参数卡的过程指定为 00h"));
                cycles.Add(Command(0xE0, "重新进入数据输出"));
            }

            cycles.Add(Wait(phase.TRrMinNs.Value, $"参数卡 {phase.Name} tRR"));
            return true;
        }

        if (card.ParameterPageBusy == "numeric" && phase.TRstReadUs is int waitUs)
        {
            cycles.Add(Wait(waitUs * 1000, "参数卡给出的参数页忙时间"));
            cycles.Add(Wait(phase.TRrMinNs.Value, $"参数卡 {phase.Name} tRR"));
            return true;
        }

        reason = $"{name} 的忙等待未给出。阵列 tR 不会拿来代替，状态过程也未给出，不发送。";
        return false;
    }

    private static LogicalCycle Command(byte opcode, string note) => new("command", opcode.ToString("X2"), null, null, null, null, note);

    private static LogicalCycle Wait(int ns, string source) => new("wait", null, null, ns, source, null, source);

    private static LogicalCycle DataOut(int length, string note) => new("data-out", null, null, null, null, length, note);

    private static LogicalCycle TimingNote(TimingPhase phase) => new(
        "note",
        null,
        null,
        phase.TAdlMinNs,
        phase.TAdlMinNs is null ? null : $"参数卡 {phase.Name} tADL",
        null,
        phase.TAdlMinNs is null
            ? "tADL 待补。Read ID 不插入编程用的 tADL，也不填一个数。"
            : $"tADL {phase.TAdlMinNs} ns 已记录。Read ID 的数据等待用 tWHR，不把 tADL 改写成数据等待。");
}
