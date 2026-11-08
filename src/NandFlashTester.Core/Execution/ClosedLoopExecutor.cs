using System.Globalization;
using System.Text.Json;
using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Patterns;
using NandFlashTester.Core.Recipes;

namespace NandFlashTester.Core.Execution;

public static class StepNames
{
    public const string Identify = "identify";
    public const string Erase = "erase";
    public const string Program = "program";
    public const string Read = "read";
    public const string Compare = "compare";

    public static readonly string[] All = [Identify, Erase, Program, Read, Compare];

    public static string Text(string name) => name switch
    {
        Identify => "读取设备信息",
        Erase => "擦除",
        Program => "图案写入",
        Read => "读回",
        Compare => "比较",
        _ => name
    };
}

public static class JobStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
    public const string Skipped = "skipped";

    public static bool IsTerminal(string status) =>
        status is Succeeded or Failed or Cancelled or Interrupted;

    public static string Text(string status) => status switch
    {
        Pending => "排队",
        Running => "执行中",
        Succeeded => "成功",
        Failed => "失败",
        Cancelled => "已取消",
        Interrupted => "已中断",
        Skipped => "未执行",
        _ => status
    };
}

public sealed record StepRecord(string Name, string Status, string Message, string? DetailJson);

public sealed record ErrorLocation(int Page, int ByteOffset, int Bit, string ExpectedHex, string ActualHex);

public sealed record ClosedLoopOutcome(
    string Status,
    int ErrorBitCount,
    int ErrorByteCount,
    IReadOnlyList<StepRecord> Steps,
    IReadOnlyList<ErrorLocation> Errors,
    string? Reason)
{
    public static ClosedLoopOutcome CancelledNow(string reason, IReadOnlyList<StepRecord>? steps = null) =>
        new(JobStates.Cancelled, 0, 0, steps ?? [], [], reason);

    public static ClosedLoopOutcome FailedNow(string reason) =>
        new(JobStates.Failed, 0, 0, [], [], reason);
}

public interface IExecutionSink
{
    Task BeforeStepAsync(string step, CancellationToken cancellationToken);

    Task StepStartedAsync(string step, CancellationToken cancellationToken);

    Task StepFinishedAsync(StepRecord record, CancellationToken cancellationToken);

    Task BeforePageAsync(string step, int page, CancellationToken cancellationToken);

    Task ReportAsync(string step, string message, string? detailJson, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class NullExecutionSink : IExecutionSink
{
    public static readonly NullExecutionSink Instance = new();

    public Task BeforeStepAsync(string step, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StepStartedAsync(string step, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StepFinishedAsync(StepRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task BeforePageAsync(string step, int page, CancellationToken cancellationToken) => Task.CompletedTask;
}

public interface IJobPacing
{
    Task WaitAsync(string step, int page, CancellationToken cancellationToken);
}

public sealed class NoDelayPacing : IJobPacing
{
    public Task WaitAsync(string step, int page, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ClosedLoopExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<ClosedLoopOutcome> ExecuteAsync(
        Recipe recipe,
        SimulatedNandDevice device,
        IExecutionSink sink,
        CancellationToken cancellationToken)
    {
        var finished = new List<StepRecord>();
        try
        {
            var identify = await RunComputedAsync(StepNames.Identify, () => Identify(device), finished, sink, cancellationToken);
            if (identify)
            {
                return await StopAsync(finished, sink, identifyFailed: true);
            }

            var erase = await RunComputedAsync(StepNames.Erase, () => Erase(recipe, device), finished, sink, cancellationToken);
            if (erase)
            {
                return await StopAsync(finished, sink, identifyFailed: true);
            }

            var program = await ProgramAsync(recipe, device, finished, sink, cancellationToken);
            if (program)
            {
                return await StopAsync(finished, sink, identifyFailed: false);
            }

            if (recipe.Inject.Mode == InjectMode.BitFlip)
            {
                device.FlipBit(
                    recipe.Target.Channel,
                    recipe.Target.Ce,
                    recipe.Target.Lun,
                    recipe.Target.Plane,
                    recipe.Target.Block,
                    recipe.Inject.Page,
                    recipe.Inject.ByteOffset,
                    recipe.Inject.Bit);
            }

            var (readCancelled, readback) = await ReadAsync(recipe, device, finished, sink, cancellationToken);
            if (readCancelled || readback is null)
            {
                return await StopAsync(finished, sink, identifyFailed: false);
            }

            var compared = Compare(recipe, readback);
            finished.Add(compared.Record);
            await sink.StepFinishedAsync(compared.Record, CancellationToken.None);

            await sink.BeforeStepAsync("finalize", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var status = compared.ErrorBitCount == 0 ? JobStates.Succeeded : JobStates.Failed;
            return new ClosedLoopOutcome(
                status,
                compared.ErrorBitCount,
                compared.ErrorByteCount,
                finished,
                compared.Errors,
                status == JobStates.Failed ? compared.Record.Message : null);
        }
        catch (OperationCanceledException)
        {
            return await StopAsync(finished, sink, identifyFailed: false);
        }
    }

    private static async Task<bool> RunComputedAsync(
        string step,
        Func<StepRecord> body,
        List<StepRecord> finished,
        IExecutionSink sink,
        CancellationToken cancellationToken)
    {
        await sink.BeforeStepAsync(step, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await sink.StepStartedAsync(step, cancellationToken);
        var record = body();
        finished.Add(record);
        await sink.StepFinishedAsync(record, CancellationToken.None);
        return record.Status == JobStates.Failed;
    }

    private static async Task<bool> ProgramAsync(
        Recipe recipe,
        SimulatedNandDevice device,
        List<StepRecord> finished,
        IExecutionSink sink,
        CancellationToken cancellationToken)
    {
        await sink.BeforeStepAsync(StepNames.Program, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await sink.StepStartedAsync(StepNames.Program, cancellationToken);
        await PaceAsync(recipe, cancellationToken);

        var programmed = 0;
        var count = recipe.Target.PageCount;
        string? cancelMessage = null;
        for (var page = recipe.Target.PageStart; page < recipe.Target.PageStart + count; page++)
        {
            try
            {
                await sink.BeforePageAsync(StepNames.Program, page, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await PaceAsync(recipe, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelMessage = $"已取消。已写入 {programmed.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} 页，未再继续写入。";
                break;
            }

            var data = PatternGenerator.Generate(
                recipe.Pattern.Kind,
                recipe.Topology.PageSizeBytes,
                recipe.Seed,
                recipe.Target.Block,
                page,
                recipe.Pattern.ConstantValue);
            device.ProgramPage(
                recipe.Target.Channel,
                recipe.Target.Ce,
                recipe.Target.Lun,
                recipe.Target.Plane,
                recipe.Target.Block,
                page,
                data);
            programmed++;
            try
            {
                await sink.ReportAsync(
                    StepNames.Program,
                    $"正在写入 {programmed.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} 页。不估计剩余时间。",
                    JsonSerializer.Serialize(new { pagesProgrammed = programmed, pageCount = count }, JsonOptions),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelMessage = $"已取消。已写入 {programmed.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} 页，未再继续写入。";
                break;
            }
        }

        var cancelled = cancelMessage is not null;
        var record = new StepRecord(
            StepNames.Program,
            cancelled ? JobStates.Cancelled : JobStates.Succeeded,
            cancelMessage ?? $"已写入 {programmed.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} 页，图案 {recipe.Pattern.KindText}，种子 {recipe.Seed.ToString(CultureInfo.InvariantCulture)}。",
            JsonSerializer.Serialize(new
            {
                pagesProgrammed = programmed,
                pageCount = count,
                pattern = recipe.Pattern.KindText,
                seed = recipe.Seed
            }, JsonOptions));
        finished.Add(record);
        await sink.StepFinishedAsync(record, CancellationToken.None);
        return cancelled;
    }

    private static async Task<(bool Cancelled, Dictionary<int, byte[]>? Readback)> ReadAsync(
        Recipe recipe,
        SimulatedNandDevice device,
        List<StepRecord> finished,
        IExecutionSink sink,
        CancellationToken cancellationToken)
    {
        await sink.BeforeStepAsync(StepNames.Read, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await sink.StepStartedAsync(StepNames.Read, cancellationToken);
        await PaceAsync(recipe, cancellationToken);

        var readback = new Dictionary<int, byte[]>();
        var count = recipe.Target.PageCount;
        string? cancelMessage = null;
        for (var page = recipe.Target.PageStart; page < recipe.Target.PageStart + count; page++)
        {
            try
            {
                await sink.BeforePageAsync(StepNames.Read, page, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await PaceAsync(recipe, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelMessage = $"已取消。已读回 {readback.Count.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} 页，未再继续。";
                break;
            }

            readback[page] = device.ReadPage(
                recipe.Target.Channel,
                recipe.Target.Ce,
                recipe.Target.Lun,
                recipe.Target.Plane,
                recipe.Target.Block,
                page);
            try
            {
                await sink.ReportAsync(
                    StepNames.Read,
                    $"正在读回 {readback.Count.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} 页。不估计剩余时间。",
                    JsonSerializer.Serialize(new { pagesRead = readback.Count, pageCount = count }, JsonOptions),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelMessage = $"已取消。已读回 {readback.Count.ToString(CultureInfo.InvariantCulture)}/{count.ToString(CultureInfo.InvariantCulture)} 页，未再继续。";
                break;
            }
        }

        var cancelled = cancelMessage is not null;
        var record = new StepRecord(
            StepNames.Read,
            cancelled ? JobStates.Cancelled : JobStates.Succeeded,
            cancelMessage ?? $"已读回 {readback.Count.ToString(CultureInfo.InvariantCulture)} 页。读回来自模拟介质。",
            JsonSerializer.Serialize(new { pagesRead = readback.Count, pageCount = count }, JsonOptions));
        finished.Add(record);
        await sink.StepFinishedAsync(record, CancellationToken.None);
        return (cancelled, cancelled ? null : readback);
    }

    private static CompareResult Compare(Recipe recipe, IReadOnlyDictionary<int, byte[]> readback)
    {
        var errors = new List<ErrorLocation>();
        var bitCount = 0;
        var byteCount = 0;
        var totalBytes = 0;
        var matchedBytes = 0;
        var truncated = false;
        for (var page = recipe.Target.PageStart; page < recipe.Target.PageStart + recipe.Target.PageCount; page++)
        {
            var expected = PatternGenerator.Generate(
                recipe.Pattern.Kind,
                recipe.Topology.PageSizeBytes,
                recipe.Seed,
                recipe.Target.Block,
                page,
                recipe.Pattern.ConstantValue);
            var actual = readback[page];
            for (var i = 0; i < expected.Length; i++)
            {
                totalBytes++;
                var xor = (byte)(expected[i] ^ actual[i]);
                if (xor == 0)
                {
                    matchedBytes++;
                    continue;
                }

                byteCount++;
                for (var bit = 0; bit < 8; bit++)
                {
                    if ((xor & (1 << bit)) == 0)
                    {
                        continue;
                    }

                    bitCount++;
                    if (errors.Count < 256)
                    {
                        errors.Add(new ErrorLocation(
                            page,
                            i,
                            bit,
                            expected[i].ToString("X2", CultureInfo.InvariantCulture),
                            actual[i].ToString("X2", CultureInfo.InvariantCulture)));
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }
        }

        var first = errors.Count == 0
            ? ""
            : FormattableString.Invariant($" 首个错误：页 {errors[0].Page} 字节 {errors[0].ByteOffset} 位 {errors[0].Bit}，期望 {errors[0].ExpectedHex}，实际 {errors[0].ActualHex}。");
        var injectNote = recipe.Inject.Mode == InjectMode.BitFlip
            ? " 此差异来自配方声明的模拟注入，不是实机失效。"
            : "";
        var message = FormattableString.Invariant($"比较 {totalBytes} 字节，不一致位数 {bitCount}。") + first + injectNote;
        var record = new StepRecord(
            StepNames.Compare,
            bitCount == 0 ? JobStates.Succeeded : JobStates.Failed,
            message,
            JsonSerializer.Serialize(new
            {
                totalBytes,
                matchedBytes,
                errorBitCount = bitCount,
                errorByteCount = byteCount,
                truncated
            }, JsonOptions));
        return new CompareResult(record, bitCount, byteCount, errors);
    }

    private static StepRecord Identify(SimulatedNandDevice device)
    {
        try
        {
            var id = device.ReadId();
            SimIdentity.ParseId(id);
            var parameterPage = device.ReadParameterPage();
            var parsed = SimParameterPage.Parse(parameterPage);
            if (!parsed.GeometryEquals(device.Topology))
            {
                return new StepRecord(StepNames.Identify, JobStates.Failed, "参数页几何与配方拓扑不一致，已停止。", null);
            }

            var message = $"模拟设备 {device.DeviceId}，厂商 {SimIdentity.Manufacturer}，型号 {SimIdentity.Model}。标识长度 {id.Length.ToString(CultureInfo.InvariantCulture)}，参数页长度 {parameterPage.Length.ToString(CultureInfo.InvariantCulture)}。{SimIdentity.Disclaimer}";
            var detail = JsonSerializer.Serialize(new
            {
                deviceId = device.DeviceId,
                kind = SimIdentity.Kind,
                manufacturer = SimIdentity.Manufacturer,
                model = SimIdentity.Model,
                idHex = SimIdentity.ToHex(id),
                parameterPageLength = parameterPage.Length,
                status = device.ReadStatus(),
                throughput = SimIdentity.Throughput,
                voltage = SimIdentity.Voltage,
                temperature = SimIdentity.Temperature
            }, JsonOptions);
            return new StepRecord(StepNames.Identify, JobStates.Succeeded, message, detail);
        }
        catch (InvalidDataException ex)
        {
            return new StepRecord(StepNames.Identify, JobStates.Failed, ex.Message, null);
        }
    }

    private static StepRecord Erase(Recipe recipe, SimulatedNandDevice device)
    {
        device.EraseBlock(recipe.Target.Channel, recipe.Target.Ce, recipe.Target.Lun, recipe.Target.Plane, recipe.Target.Block);
        return new StepRecord(
            StepNames.Erase,
            JobStates.Succeeded,
            FormattableString.Invariant($"已擦除块 {recipe.Target.Block}。块内页恢复为 FF。此为模拟介质。"),
            JsonSerializer.Serialize(new { block = recipe.Target.Block, erasedByte = "FF" }, JsonOptions));
    }

    private static async Task<ClosedLoopOutcome> StopAsync(List<StepRecord> finished, IExecutionSink sink, bool identifyFailed)
    {
        var reason = identifyFailed
            ? finished.LastOrDefault()?.Message ?? "步骤失败，后续步骤未执行。"
            : "任务已取消，取消后没有继续写入。";
        var status = identifyFailed ? JobStates.Failed : JobStates.Cancelled;
        var skipMessage = identifyFailed ? "前序步骤失败，未执行。" : "已取消，未执行。";
        var skipStatus = identifyFailed ? JobStates.Skipped : JobStates.Skipped;
        foreach (var name in StepNames.All)
        {
            if (finished.Any(step => step.Name == name))
            {
                continue;
            }

            var record = new StepRecord(name, skipStatus, skipMessage, null);
            finished.Add(record);
            await sink.StepFinishedAsync(record, CancellationToken.None);
        }

        return new ClosedLoopOutcome(status, 0, 0, finished, [], reason);
    }

    private static Task PaceAsync(Recipe recipe, CancellationToken cancellationToken) =>
        recipe.PaceMs > 0 ? Task.Delay(recipe.PaceMs, cancellationToken) : Task.CompletedTask;

    private sealed record CompareResult(
        StepRecord Record,
        int ErrorBitCount,
        int ErrorByteCount,
        IReadOnlyList<ErrorLocation> Errors);
}
