using System.Text;
using System.Text.Json;
using NandFlashTester.Core;
using NandFlashTester.Core.Platform;
using NandFlashTester.Core.Recipes;
using NandFlashTester.Tests.Support;

namespace NandFlashTester.Tests;

public class PlatformSessionTests
{
    [Fact]
    public async Task Blocked_hardware_path_emits_nothing()
    {
        var transport = new BlockedHardwareTransport();
        var outcomes = await SessionEngine.RunRecipeAsync(transport, TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.Equal(5, outcomes.Count);
        Assert.All(outcomes, outcome =>
        {
            Assert.False(outcome.Sent);
            Assert.False(outcome.HardwareAccepted);
            Assert.Equal(EvidenceClasses.SoftwareContract, outcome.EvidenceClass);
            Assert.Equal("Rejected", outcome.State);
        });
        Assert.Equal(0, transport.EmitCount);
    }

    [Fact]
    public async Task Dangerous_intent_is_disabled()
    {
        var outcome = await SessionEngine.RunAsync("set-voltage", new BlockedHardwareTransport(), TimeSpan.FromMilliseconds(20), CancellationToken.None);
        Assert.Equal("Rejected", outcome.State);
        Assert.False(outcome.Sent);
        Assert.Contains("禁用", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripted_timeout_is_not_a_hardware_result()
    {
        var transport = new ScriptedTransport { Delay = TimeSpan.FromSeconds(2) };
        var outcome = await SessionEngine.RunAsync("read", transport, TimeSpan.FromMilliseconds(30), CancellationToken.None);
        Assert.Equal("TimedOut", outcome.State);
        Assert.True(outcome.Sent);
        Assert.False(outcome.HardwareAccepted);
        Assert.Equal(EvidenceClasses.AutomatedTest, outcome.EvidenceClass);
    }

    [Fact]
    public async Task Scripted_cancel_stops_the_recipe()
    {
        var transport = new ScriptedTransport { Delay = TimeSpan.FromSeconds(2) };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var outcomes = await SessionEngine.RunRecipeAsync(transport, TimeSpan.FromSeconds(2), cancel.Token);
        Assert.Equal("Cancelled", outcomes[0].State);
        Assert.Equal(1, transport.EmitCount);
        Assert.All(outcomes, outcome => Assert.False(outcome.HardwareAccepted));
    }

    [Fact]
    public async Task Production_session_rejects_wire_io_and_ignores_the_fixture()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath(), platformMode: "Production");
        using var client = app.CreateClient();
        var created = await client.PostAsync("/api/sessions", new StringContent(""));
        Assert.Equal(201, (int)created.StatusCode);
        using var session = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var sessionId = session.RootElement.GetProperty("sessionId").GetString()!;
        Assert.False(session.RootElement.GetProperty("hardwareAccepted").GetBoolean());
        Assert.Equal(0, session.RootElement.GetProperty("bytesEmitted").GetInt32());

        var executed = await client.PostAsync($"/api/sessions/{sessionId}/execute", new StringContent(RecipePresets.SuccessJson, Encoding.UTF8, "application/json"));
        Assert.Equal(409, (int)executed.StatusCode);
        using var body = JsonDocument.Parse(await executed.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("executed").GetBoolean());
        Assert.False(body.RootElement.GetProperty("fallbackToFixture").GetBoolean());
        Assert.Equal(0, body.RootElement.GetProperty("bytesEmitted").GetInt32());
        using var after = JsonDocument.Parse(await (await client.GetAsync($"/api/sessions/{sessionId}")).Content.ReadAsStringAsync());
        Assert.Equal(0, after.RootElement.GetProperty("commands").GetArrayLength());

        var fixture = await client.PostAsync("/api/jobs", new StringContent(RecipePresets.SuccessJson, Encoding.UTF8, "application/json"));
        Assert.Equal(403, (int)fixture.StatusCode);

        using var discovery = JsonDocument.Parse(await (await client.GetAsync("/api/discovery")).Content.ReadAsStringAsync());
        Assert.False(discovery.RootElement.GetProperty("connected").GetBoolean());
        Assert.False(discovery.RootElement.GetProperty("probed").GetBoolean());
        Assert.Empty(discovery.RootElement.GetProperty("devices").EnumerateArray());
        Assert.Contains("不会猜测地址", discovery.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sim-dut", discovery.RootElement.ToString(), StringComparison.Ordinal);
        using var platform = JsonDocument.Parse(await (await client.GetAsync("/api/platform")).Content.ReadAsStringAsync());
        Assert.Equal(ProductIdentity.Name, platform.RootElement.GetProperty("product").GetString());

        using var materials = JsonDocument.Parse(await (await client.GetAsync("/api/materials")).Content.ReadAsStringAsync());
        Assert.False(materials.RootElement.GetProperty("realConfigEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, materials.RootElement.GetProperty("partNumber").ValueKind);
    }
}

public sealed class ScriptedTransport : ITransport
{
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    public bool MayEmitBytes => true;

    public int EmitCount { get; private set; }

    public async Task<TransportReply> ExchangeAsync(string intent, CancellationToken cancellationToken)
    {
        EmitCount++;
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new TransportReply(true, "自动化测试应答，不是实机结果。");
    }
}
