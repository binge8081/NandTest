using System.Text;
using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Execution;
using NandFlashTester.Core.Patterns;
using NandFlashTester.Core.Recipes;

namespace NandFlashTester.Tests;

public class ClosedLoopExecutorTests
{
    private readonly ClosedLoopExecutor _executor = new();

    [Fact]
    public async Task Success_readback_matches_every_byte()
    {
        var recipe = RecipePresets.Success;
        var device = new SimulatedNandDevice(SimIdentity.StandInId, recipe.Topology);
        var outcome = await _executor.ExecuteAsync(recipe, device, NullExecutionSink.Instance, CancellationToken.None);

        Assert.Equal(JobStates.Succeeded, outcome.Status);
        Assert.Equal(0, outcome.ErrorBitCount);
        Assert.Equal(recipe.Target.PageCount, device.ProgramCalls);
        for (var page = recipe.Target.PageStart; page < recipe.Target.PageStart + recipe.Target.PageCount; page++)
        {
            var expected = PatternGenerator.Generate(PatternKind.Increment, recipe.Topology.PageSizeBytes, recipe.Seed, recipe.Target.Block, page);
            Assert.Equal(expected, device.ReadPage(0, 0, 0, 0, recipe.Target.Block, page));
        }
    }

    [Fact]
    public async Task Same_seed_produces_the_same_readback_twice()
    {
        var first = await RunAsync(RecipePresets.Success);
        var second = await RunAsync(RecipePresets.Success);
        Assert.Equal(Dump(first), Dump(second));
    }

    [Fact]
    public async Task Injected_bit_is_counted_at_the_declared_location()
    {
        var recipe = RecipePresets.BitFlip;
        var device = new SimulatedNandDevice(SimIdentity.StandInId, recipe.Topology);
        var outcome = await _executor.ExecuteAsync(recipe, device, NullExecutionSink.Instance, CancellationToken.None);

        Assert.Equal(JobStates.Failed, outcome.Status);
        Assert.Equal(1, outcome.ErrorBitCount);
        Assert.Equal(1, outcome.ErrorByteCount);
        var error = Assert.Single(outcome.Errors);
        Assert.Equal(1, error.Page);
        Assert.Equal(3, error.ByteOffset);
        Assert.Equal(0, error.Bit);
        Assert.Equal("4A", error.ExpectedHex);
        Assert.Equal("4B", error.ActualHex);
        Assert.Contains("模拟注入", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_before_the_second_page_does_not_program_it()
    {
        var recipe = RecipePresets.Success;
        var device = new SimulatedNandDevice(SimIdentity.StandInId, recipe.Topology);
        var outcome = await _executor.ExecuteAsync(recipe, device, new CancelOnProgramPageSink(1), CancellationToken.None);

        Assert.Equal(JobStates.Cancelled, outcome.Status);
        Assert.Equal(1, device.ProgramCalls);
        Assert.Equal(JobStates.Cancelled, outcome.Steps.Single(step => step.Name == StepNames.Program).Status);
        Assert.Equal(JobStates.Skipped, outcome.Steps.Single(step => step.Name == StepNames.Read).Status);
        Assert.Equal(JobStates.Skipped, outcome.Steps.Single(step => step.Name == StepNames.Compare).Status);
        Assert.Contains("未再继续写入", outcome.Steps.Single(step => step.Name == StepNames.Program).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_boundary_bytes_survive_the_closed_loop()
    {
        const string json = """
            {"schema":"nand.sim.recipe/1","name":"页边界","version":"1.0.0","seed":0,"topology":{"channels":1,"ces":1,"luns":1,"planes":1,"blocks":1,"pagesPerBlock":4,"pageSizeBytes":16},"pattern":{"kind":"increment"},"target":{"channel":0,"ce":0,"lun":0,"plane":0,"block":0,"pageStart":0,"pageCount":2},"inject":{"mode":"none"}}
            """;
        var recipe = RecipeValidator.Validate(Encoding.UTF8.GetBytes(json)).Recipe!;
        var device = new SimulatedNandDevice(SimIdentity.StandInId, recipe.Topology);
        var outcome = await _executor.ExecuteAsync(recipe, device, NullExecutionSink.Instance, CancellationToken.None);
        Assert.Equal(JobStates.Succeeded, outcome.Status);
        Assert.Equal((byte)15, device.ReadPage(0, 0, 0, 0, 0, 0)[15]);
        Assert.Equal((byte)16, device.ReadPage(0, 0, 0, 0, 0, 1)[0]);
    }

    private async Task<SimulatedNandDevice> RunAsync(Recipe recipe)
    {
        var device = new SimulatedNandDevice(SimIdentity.StandInId, recipe.Topology);
        var outcome = await _executor.ExecuteAsync(recipe, device, NullExecutionSink.Instance, CancellationToken.None);
        Assert.Equal(JobStates.Succeeded, outcome.Status);
        return device;
    }

    private static byte[] Dump(SimulatedNandDevice device)
    {
        var pages = new List<byte>();
        for (var page = 0; page < device.Topology.PagesPerBlock; page++)
        {
            pages.AddRange(device.ReadPage(0, 0, 0, 0, 0, page));
        }

        return pages.ToArray();
    }

    private sealed class CancelOnProgramPageSink(int page) : IExecutionSink
    {
        public Task BeforeStepAsync(string step, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StepStartedAsync(string step, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StepFinishedAsync(StepRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task BeforePageAsync(string step, int pageIndex, CancellationToken cancellationToken)
        {
            if (step == StepNames.Program && pageIndex == page)
            {
                throw new OperationCanceledException();
            }

            return Task.CompletedTask;
        }
    }
}
