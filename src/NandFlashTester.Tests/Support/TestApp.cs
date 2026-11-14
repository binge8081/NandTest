using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NandFlashTester.Core.Execution;

namespace NandFlashTester.Tests.Support;

public sealed class GatePacing : IJobPacing
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GatePacing(string step, int page)
    {
        Step = step;
        Page = page;
    }

    public string Step { get; }

    public int Page { get; }

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    public async Task WaitAsync(string step, int page, CancellationToken cancellationToken)
    {
        if (step == Step && page == Page)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }
}

public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly IJobPacing _pacing;

    public TestApp(string databasePath, IJobPacing? pacing = null, string platformMode = "AutomatedTest", IReadOnlyDictionary<string, string?>? settings = null)
    {
        DatabasePath = databasePath;
        PlatformMode = platformMode;
        Settings = settings;
        _pacing = pacing ?? new NoDelayPacing();
    }

    public IReadOnlyDictionary<string, string?>? Settings { get; }

    public string PlatformMode { get; }

    public string DatabasePath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["DatabasePath"] = DatabasePath,
                ["Platform:Mode"] = PlatformMode
            };
            if (Settings is not null)
            {
                foreach (var pair in Settings)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            config.AddInMemoryCollection(values);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IJobPacing>();
            services.AddSingleton(_pacing);
        });
    }

    public static string NewDatabasePath()
    {
        var directory = Directory.CreateTempSubdirectory("nand-sim-");
        return Path.Combine(directory.FullName, "closed-loop.db");
    }
}
