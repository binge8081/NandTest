using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NandFlashTester.Api.Data;
using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Execution;
using NandFlashTester.Core.Recipes;

namespace NandFlashTester.Api.Jobs;

public sealed class JobQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    public void Enqueue(string jobId)
    {
        if (!_channel.Writer.TryWrite(jobId))
        {
            throw new InvalidOperationException("任务队列不可用。");
        }
    }

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed record DeviceResolution(string RequestedDeviceId, string ActualDeviceId, bool Substituted, string? Reason);

public static class SimulationDeviceCatalog
{
    public static DeviceResolution Resolve(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested == SimIdentity.StandInId)
        {
            return new DeviceResolution(SimIdentity.StandInId, SimIdentity.StandInId, false, null);
        }

        if (!IsSafe(requested))
        {
            return new DeviceResolution(
                "invalid-device-id",
                SimIdentity.StandInId,
                true,
                "请求的硬件标识无效。已改用模拟替身 sim-dut-1，结果不是该硬件的实测。");
        }

        return new DeviceResolution(
            requested,
            SimIdentity.StandInId,
            true,
            $"未知硬件 {requested} 没有驱动。已改用模拟替身 sim-dut-1。结果不是该硬件的实测性能、电压、温度或量产结论。");
    }

    private static bool IsSafe(string id) =>
        id.Length <= 64 && id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
}

public sealed class PersistSink(SqliteJobStore store, string jobId, IJobPacing pacing) : IExecutionSink
{
    public Task BeforeStepAsync(string step, CancellationToken cancellationToken)
    {
        if (store.IsCancelRequested(jobId))
        {
            throw new OperationCanceledException();
        }

        return Task.CompletedTask;
    }

    public Task StepStartedAsync(string step, CancellationToken cancellationToken)
    {
        store.MarkStepStarted(jobId, step);
        return Task.CompletedTask;
    }

    public Task StepFinishedAsync(StepRecord record, CancellationToken cancellationToken)
    {
        store.MarkStepFinished(jobId, record);
        return Task.CompletedTask;
    }

    public Task ReportAsync(string step, string message, string? detailJson, CancellationToken cancellationToken)
    {
        store.MarkStepProgress(jobId, step, message, detailJson);
        if (store.IsCancelRequested(jobId))
        {
            throw new OperationCanceledException();
        }

        return Task.CompletedTask;
    }

    public async Task BeforePageAsync(string step, int page, CancellationToken cancellationToken)
    {
        await pacing.WaitAsync(step, page, cancellationToken);
        if (store.IsCancelRequested(jobId))
        {
            throw new OperationCanceledException();
        }
    }
}

public sealed class JobService(SqliteJobStore store, JobQueue queue, ILogger<JobService> logger)
{
    private readonly object _createGate = new();

    public (int Status, object Body) Create(byte[] recipeUtf8, string? deviceId)
    {
        var validation = RecipeValidator.Validate(recipeUtf8);
        if (!validation.Ok || validation.Recipe is null)
        {
            return (StatusCodes.Status400BadRequest, new
            {
                error = "recipe_rejected",
                message = "配方未通过受限校验，未执行。",
                executed = false,
                details = validation.Errors
            });
        }

        var resolution = SimulationDeviceCatalog.Resolve(deviceId);
        var recipe = validation.Recipe;
        var snapshot = JsonSerializer.Serialize(new
        {
            deviceId = resolution.ActualDeviceId,
            kind = SimIdentity.Kind,
            kindText = SimIdentity.KindText,
            manufacturer = SimIdentity.Manufacturer,
            model = SimIdentity.Model,
            disclaimer = SimIdentity.Disclaimer,
            requestedDeviceId = resolution.RequestedDeviceId,
            substituted = resolution.Substituted,
            substitutionReason = resolution.Reason,
            throughput = SimIdentity.Throughput,
            voltage = SimIdentity.Voltage,
            temperature = SimIdentity.Temperature,
            topology = new
            {
                recipe.Topology.Channels,
                recipe.Topology.Ces,
                recipe.Topology.Luns,
                recipe.Topology.Planes,
                recipe.Topology.Blocks,
                recipe.Topology.PagesPerBlock,
                recipe.Topology.PageSizeBytes
            }
        });

        var jobId = "job-" + Guid.NewGuid().ToString("N");
        lock (_createGate)
        {
            var busy = store.FindActiveJob(resolution.ActualDeviceId);
            if (busy is not null)
            {
                return (StatusCodes.Status409Conflict, new
                {
                    error = "device_busy",
                    message = $"模拟设备 {resolution.ActualDeviceId} 正在执行任务 {busy}，已拒绝并发。",
                    executed = false,
                    jobId = busy
                });
            }

            store.Insert(new JobInsert(jobId, recipe, resolution.RequestedDeviceId, resolution.ActualDeviceId, resolution.Substituted, resolution.Reason, snapshot));
        }

        queue.Enqueue(jobId);
        logger.LogInformation("已排队模拟任务 {JobId}", jobId);
        return (StatusCodes.Status201Created, new
        {
            jobId,
            status = JobStates.Pending,
            statusText = JobStates.Text(JobStates.Pending),
            deviceId = resolution.ActualDeviceId,
            deviceSubstituted = resolution.Substituted,
            substitutionReason = resolution.Reason,
            executed = true
        });
    }

    public (int Status, object Body) Cancel(string jobId)
    {
        var existing = store.Get(jobId);
        if (existing is null)
        {
            return (StatusCodes.Status404NotFound, new { error = "not_found", message = "任务不存在。" });
        }

        if (!JobStates.IsTerminal(existing.Status))
        {
            store.RequestCancel(jobId);
            existing = store.Get(jobId) ?? existing;
        }

        return (StatusCodes.Status200OK, JobViews.ToDetail(existing));
    }
}

public sealed class JobWorker(
    JobQueue queue,
    SqliteJobStore store,
    IJobPacing pacing,
    ILogger<JobWorker> logger) : BackgroundService
{
    private readonly ClosedLoopExecutor _executor = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                store.FailUnexpected(jobId, "服务正在停止。未完成任务不会在重启后继续写入。");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "任务 {JobId} 执行失败", jobId);
                store.FailUnexpected(jobId, "任务执行出现意外错误，已停止。未继续写入。");
            }
        }
    }

    private async Task RunAsync(string jobId, CancellationToken stoppingToken)
    {
        var job = store.Get(jobId);
        if (job is null || JobStates.IsTerminal(job.Status))
        {
            return;
        }

        if (job.CancelRequested)
        {
            store.SkipUnfinished(jobId, JobStates.Skipped, "已取消，未执行。");
            store.Complete(jobId, ClosedLoopOutcome.CancelledNow("排队期间已取消，未执行写入。"));
            return;
        }

        if (!store.TryMarkRunning(jobId))
        {
            if (store.IsCancelRequested(jobId))
            {
                store.SkipUnfinished(jobId, JobStates.Skipped, "已取消，未执行。");
                store.Complete(jobId, ClosedLoopOutcome.CancelledNow("排队期间已取消，未执行写入。"));
            }

            return;
        }

        var validation = RecipeValidator.Validate(Encoding.UTF8.GetBytes(job.RecipeSnapshot));
        if (!validation.Ok || validation.Recipe is null)
        {
            store.SkipUnfinished(jobId, JobStates.Skipped, "配方快照无法再次校验，未执行。");
            store.Complete(jobId, ClosedLoopOutcome.FailedNow("配方快照校验失败，未执行。"));
            return;
        }

        var device = new SimulatedNandDevice(job.DeviceId, validation.Recipe.Topology);
        var sink = new PersistSink(store, jobId, pacing);
        var outcome = await _executor.ExecuteAsync(validation.Recipe, device, sink, stoppingToken);
        store.Complete(jobId, outcome);
    }
}

public static class JobOverview
{
    public static readonly string[] UnimplementedAnalyses =
    [
        "错误分布曲线：未实现",
        "UBER/RBER：未实现",
        "忙时间：未实现",
        "电压或温度曲线：未实现"
    ];

    public static string Conclusion(string status) => status switch
    {
        JobStates.Succeeded => "通过",
        JobStates.Failed => "失败",
        JobStates.Cancelled => "已取消",
        JobStates.Interrupted => "已中断",
        JobStates.Running => "执行中",
        JobStates.Pending => "排队",
        JobStates.Skipped => "未测",
        _ => status
    };

    public static string Symbol(string status) => status switch
    {
        JobStates.Succeeded => "✓",
        JobStates.Failed => "✕",
        JobStates.Cancelled => "■",
        JobStates.Interrupted => "!",
        JobStates.Running => "…",
        JobStates.Pending => "○",
        JobStates.Skipped => "–",
        _ => "·"
    };

    public static string Coverage(string status) => status switch
    {
        JobStates.Succeeded or JobStates.Failed => "已测",
        JobStates.Cancelled => "已取消",
        JobStates.Running => "执行中",
        _ => "未测"
    };

    public static object Build(StoredJob job)
    {
        var steps = job.Steps;
        var totalSteps = steps.Count == 0 ? StepNames.All.Length : steps.Count;
        var completedSteps = steps.Count(step => step.Status is JobStates.Succeeded or JobStates.Failed or JobStates.Cancelled or JobStates.Skipped);
        var current = steps.FirstOrDefault(step => step.Status == JobStates.Running)
            ?? steps.LastOrDefault(step => step.Status is not JobStates.Pending and not JobStates.Skipped);
        var program = ReadPair(steps, StepNames.Program, "pagesProgrammed", "pageCount");
        var read = ReadPair(steps, StepNames.Read, "pagesRead", "pageCount");
        var compare = ReadPair(steps, StepNames.Compare, "matchedBytes", "totalBytes");
        string? pageProgress = null;
        if (current?.Name == StepNames.Program && program.Done is not null && program.Total is not null)
        {
            pageProgress = $"图案写入 {program.Done}/{program.Total} 页";
        }
        else if (current?.Name == StepNames.Read && read.Done is not null && read.Total is not null)
        {
            pageProgress = $"读回 {read.Done}/{read.Total} 页";
        }
        else if (program.Done is not null && program.Total is not null && job.Status is not JobStates.Pending)
        {
            pageProgress = $"图案写入 {program.Done}/{program.Total} 页";
        }

        var cancelFeedback = job.CancelRequested && !JobStates.IsTerminal(job.Status)
            ? "已收到取消。当前这一页结束后不再写入。不估计剩余时间。"
            : job.Status == JobStates.Cancelled
                ? "取消已完成，状态保持已取消，后续页面不再写入。"
                : null;

        return new
        {
            conclusion = Conclusion(job.Status),
            conclusionSymbol = Symbol(job.Status),
            simulationLabel = "模拟",
            simulationSymbol = "◇",
            currentStep = current is null ? "尚未开始" : StepNames.Text(current.Name),
            currentStepStatus = current is null ? JobStates.Text(job.Status) : JobStates.Text(current.Status),
            currentStepCoverage = current is null ? "未测" : Coverage(current.Status),
            completedSteps,
            totalSteps,
            stepProgressText = $"{completedSteps}/{totalSteps} 步",
            pageProgressText = pageProgress,
            eta = (string?)null,
            etaNote = "总耗时未测量，不估算剩余时间。",
            reason = Reason(job),
            nextStep = NextStep(job),
            cancelFeedback,
            errorBitCount = job.ErrorBitCount,
            errorByteCount = job.ErrorByteCount,
            matchedBytes = compare.Done,
            totalBytes = compare.Total,
            pagesProgrammed = program.Done,
            pageCount = program.Total,
            unimplemented = UnimplementedAnalyses
        };
    }

    private static string Reason(StoredJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.FailureReason))
        {
            return job.FailureReason;
        }

        if (job.CancelRequested && !JobStates.IsTerminal(job.Status))
        {
            return "操作员请求取消，任务还在收尾。";
        }

        return job.Status switch
        {
            JobStates.Succeeded => "读回与图案逐字节一致，错误位数为 0。这是模拟介质的结果。",
            JobStates.Pending => "任务已排队，还没有开始写入。",
            JobStates.Running => "模拟擦写读正在进行。",
            _ => "没有额外原因。"
        };
    }

    private static string NextStep(StoredJob job) => job.Status switch
    {
        JobStates.Succeeded => "可展开逐步结果，或用任务编号下载 CSV 核对计数。不要把模拟通过当成量产结论。",
        JobStates.Failed => "展开错误位置核对页、字节和位。确认是否为配方里的模拟注入，然后改配方重新运行。",
        JobStates.Cancelled => "后续写入已经停止。若需要完整比较，请重新运行。",
        JobStates.Interrupted => "服务重启时任务未完成，本版不会续跑。请重新运行。",
        JobStates.Running when job.CancelRequested => "请等待状态变为已取消。不要再次写入同一设备。",
        JobStates.Running => "可点取消。取消后尚未开始的页不会写入。",
        JobStates.Pending => "等待进入执行。设备正被占用时不要重复启动。",
        _ => "查看原因后修正条件，再运行一次。"
    };

    private static (int? Done, int? Total) ReadPair(IReadOnlyList<StoredStep> steps, string name, string doneName, string totalName)
    {
        var detail = steps.FirstOrDefault(step => step.Name == name)?.DetailJson;
        if (string.IsNullOrWhiteSpace(detail))
        {
            return (null, null);
        }

        using var document = JsonDocument.Parse(detail);
        return (ReadInt(document.RootElement, doneName), ReadInt(document.RootElement, totalName));
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
}

public static class JobViews
{
    public static object ToDetail(StoredJob job) => new
    {
        jobId = job.JobId,
        status = job.Status,
        statusText = JobStates.Text(job.Status),
        seed = job.Seed,
        recipeName = job.RecipeName,
        recipeVersion = job.RecipeVersion,
        recipeSchema = job.RecipeSchema,
        recipeSha256 = job.RecipeSha256,
        recipeSnapshot = job.RecipeSnapshot,
        requestedDeviceId = job.RequestedDeviceId,
        deviceId = job.DeviceId,
        deviceKind = job.DeviceKind,
        deviceKindText = SimIdentity.KindText,
        deviceSubstituted = job.DeviceSubstituted,
        substitutionReason = job.SubstitutionReason,
        errorBitCount = job.ErrorBitCount,
        errorByteCount = job.ErrorByteCount,
        failureReason = job.FailureReason,
        resultNature = job.ResultNature,
        cancelRequested = job.CancelRequested,
        createdAt = job.CreatedAt,
        startedAt = job.StartedAt,
        finishedAt = job.FinishedAt,
        steps = job.Steps.Select(ToStep).ToList(),
        overview = JobOverview.Build(job),
        errors = job.Errors.Select(error => new
        {
            page = error.Page,
            byteOffset = error.ByteOffset,
            bit = error.Bit,
            expectedHex = error.ExpectedHex,
            actualHex = error.ActualHex
        }).ToList()
    };

    public static object ToSummary(StoredJob job) => new
    {
        jobId = job.JobId,
        status = job.Status,
        statusText = JobStates.Text(job.Status),
        seed = job.Seed,
        recipeName = job.RecipeName,
        recipeSha256 = job.RecipeSha256,
        deviceId = job.DeviceId,
        deviceKindText = SimIdentity.KindText,
        deviceSubstituted = job.DeviceSubstituted,
        errorBitCount = job.ErrorBitCount,
        conclusion = JobOverview.Conclusion(job.Status),
        conclusionSymbol = JobOverview.Symbol(job.Status),
        simulationLabel = "模拟",
        resultNature = job.ResultNature,
        createdAt = job.CreatedAt,
        finishedAt = job.FinishedAt
    };

    private static object ToStep(StoredStep step) => new
    {
        index = step.Index,
        name = step.Name,
        nameText = StepNames.Text(step.Name),
        status = step.Status,
        statusText = JobStates.Text(step.Status),
        coverageText = JobOverview.Coverage(step.Status),
        message = step.Message,
        detail = ParseDetail(step.DetailJson),
        startedAt = step.StartedAt,
        finishedAt = step.FinishedAt
    };

    private static object? ParseDetail(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public static class CsvExport
{
    public static readonly string[] Headers =
    [
        "任务编号", "配方名称", "配方版本", "配方哈希", "种子",
        "请求设备", "实际设备", "设备类型", "未知硬件已替换", "替换说明",
        "任务状态", "结论", "错误位数", "错误字节数", "结果性质",
        "步骤序号", "步骤名称", "步骤状态", "步骤覆盖", "步骤说明",
        "页", "字节偏移", "位", "期望值", "实际值"
    ];

    public static string Build(StoredJob job)
    {
        var builder = new StringBuilder();
        builder.Append('\uFEFF');
        builder.AppendLine(string.Join(',', Headers));
        foreach (var step in job.Steps)
        {
            if (step.Name == StepNames.Compare && job.Errors.Count > 0)
            {
                foreach (var error in job.Errors)
                {
                    builder.AppendLine(Row(job, step, error));
                }
            }
            else
            {
                builder.AppendLine(Row(job, step, null));
            }
        }

        return builder.ToString();
    }

    private static string Row(StoredJob job, StoredStep step, StoredError? error)
    {
        var cells = new[]
        {
            job.JobId,
            job.RecipeName,
            job.RecipeVersion,
            job.RecipeSha256,
            job.Seed.ToString(CultureInfo.InvariantCulture),
            job.RequestedDeviceId,
            job.DeviceId,
            SimIdentity.KindText,
            job.DeviceSubstituted ? "是" : "否",
            job.SubstitutionReason ?? "",
            JobStates.Text(job.Status),
            JobOverview.Conclusion(job.Status),
            job.ErrorBitCount.ToString(CultureInfo.InvariantCulture),
            job.ErrorByteCount.ToString(CultureInfo.InvariantCulture),
            job.ResultNature,
            step.Index.ToString(CultureInfo.InvariantCulture),
            StepNames.Text(step.Name),
            JobStates.Text(step.Status),
            JobOverview.Coverage(step.Status),
            step.Message ?? "",
            error is null ? "" : error.Page.ToString(CultureInfo.InvariantCulture),
            error is null ? "" : error.ByteOffset.ToString(CultureInfo.InvariantCulture),
            error is null ? "" : error.Bit.ToString(CultureInfo.InvariantCulture),
            error?.ExpectedHex ?? "",
            error?.ActualHex ?? ""
        };
        return string.Join(',', cells.Select(Cell));
    }

    private static string Cell(string value)
    {
        var safe = value;
        if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@')
        {
            safe = "'" + safe;
        }

        if (safe.Contains('"') || safe.Contains(',') || safe.Contains('\n') || safe.Contains('\r'))
        {
            return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return safe;
    }
}
