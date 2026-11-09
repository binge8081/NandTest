using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NandFlashTester.Api.Data;
using NandFlashTester.Tests.Support;

namespace NandFlashTester.Tests;

public class LotLifecycleTests
{
    [Fact]
    public async Task Locked_config_pins_retest_share_and_raw_limit()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath(), platformMode: "Production");
        using var client = app.CreateClient();
        var draft = await client.PostAsJsonAsync("/api/lots", new { configId = "missing", version = 1, industrialPlanned = 0, totalPlanned = 10 });
        Assert.Equal(400, (int)draft.StatusCode);

        var created = await client.PostAsJsonAsync("/api/sku-configs", new { name = "lot-a", retestLimit = 1, temperatureCandidate = "industrial", rawEnabled = false, rawQuotaBytes = 0 });
        using var config = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var configId = config.RootElement.GetProperty("configId").GetString()!;
        var version = config.RootElement.GetProperty("version").GetInt32();
        Assert.False(config.RootElement.GetProperty("devicePermissionGranted").GetBoolean());
        var locked = await client.PostAsync($"/api/sku-configs/{configId}/lock?version={version}", new StringContent(""));
        Assert.Equal(200, (int)locked.StatusCode);

        var overShare = await client.PostAsJsonAsync("/api/lots", new { configId, version, industrialPlanned = 2, totalPlanned = 10 });
        Assert.Equal(400, (int)overShare.StatusCode);
        Assert.Contains("10%", await overShare.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var opened = await client.PostAsJsonAsync("/api/lots", new { configId, version, industrialPlanned = 1, totalPlanned = 10 });
        using var lot = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
        var lotId = lot.RootElement.GetProperty("lotId").GetString()!;
        var sha = lot.RootElement.GetProperty("configSha256").GetString()!;
        Assert.False(lot.RootElement.GetProperty("gradesAssigned").GetBoolean());

        var recorded = await client.PostAsJsonAsync($"/api/lots/{lotId}/units", new { label = "U1" });
        Assert.Equal(200, (int)recorded.StatusCode);
        var retest = await client.PostAsync($"/api/lots/{lotId}/units/U1/retest", new StringContent(""));
        Assert.Equal(200, (int)retest.StatusCode);
        var over = await client.PostAsync($"/api/lots/{lotId}/units/U1/retest", new StringContent(""));
        Assert.Equal(400, (int)over.StatusCode);
        Assert.Contains("上限 1", await over.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var raw = new HttpRequestMessage(HttpMethod.Post, $"/api/lots/{lotId}/units/U1/attempts/1/raw")
        {
            Content = new ByteArrayContent(new byte[32])
        };
        var rejected = await client.SendAsync(raw);
        Assert.Equal(400, (int)rejected.StatusCode);
        Assert.Contains("不读取", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var csv = await (await client.GetAsync($"/api/lots/{lotId}/export.csv")).Content.ReadAsStringAsync();
        Assert.Contains(sha, csv, StringComparison.Ordinal);
        Assert.Contains("未分档", csv, StringComparison.Ordinal);
        Assert.Contains("executed_rate:executed_rate_mt_per_s", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("调用者自填", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raw_length_is_rejected_before_it_can_exceed_the_host_quota()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath(), platformMode: "Production");
        using var client = app.CreateClient();
        var created = await client.PostAsJsonAsync("/api/sku-configs", new { name = "raw-on", retestLimit = 2, temperatureCandidate = "", rawEnabled = true, rawQuotaBytes = 16 });
        using var config = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var configId = config.RootElement.GetProperty("configId").GetString()!;
        var version = config.RootElement.GetProperty("version").GetInt32();
        Assert.Equal(200, (int)(await client.PostAsync($"/api/sku-configs/{configId}/lock?version={version}", new StringContent(""))).StatusCode);
        using var opened = JsonDocument.Parse(await (await client.PostAsJsonAsync("/api/lots", new { configId, version, industrialPlanned = 0, totalPlanned = 10 })).Content.ReadAsStringAsync());
        var lotId = opened.RootElement.GetProperty("lotId").GetString()!;
        Assert.Equal(200, (int)(await client.PostAsJsonAsync($"/api/lots/{lotId}/units", new { label = "U1" })).StatusCode);

        var tooBig = new HttpRequestMessage(HttpMethod.Post, $"/api/lots/{lotId}/units/U1/attempts/1/raw")
        {
            Content = new ByteArrayContent(new byte[64])
        };
        var rejected = await client.SendAsync(tooBig);
        Assert.Equal(400, (int)rejected.StatusCode);
        Assert.Contains("读取前拒绝", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var accepted = new HttpRequestMessage(HttpMethod.Post, $"/api/lots/{lotId}/units/U1/attempts/1/raw")
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes("0123456789abcdef"))
        };
        var saved = await client.SendAsync(accepted);
        Assert.Equal(200, (int)saved.StatusCode);
        using var body = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        var attempt = body.RootElement.GetProperty("attempts").EnumerateArray().Single();
        Assert.Equal(16, attempt.GetProperty("rawLength").GetInt32());
        Assert.False(attempt.GetProperty("measured").GetBoolean());
        Assert.Contains("raw_source", attempt.GetProperty("uncovered").ToString(), StringComparison.Ordinal);

        Assert.Equal(200, (int)(await client.PostAsync($"/api/lots/{lotId}/units/U1/retest", new StringContent(""))).StatusCode);
        Assert.Equal(200, (int)(await client.PostAsync($"/api/lots/{lotId}/units/U1/retest", new StringContent(""))).StatusCode);
        var stopped = await client.PostAsync($"/api/lots/{lotId}/units/U1/retest", new StringContent(""));
        Assert.Equal(400, (int)stopped.StatusCode);
        Assert.Contains("上限 2", await stopped.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void Host_restart_does_not_count_an_uncommitted_attempt()
    {
        var path = TestApp.NewDatabasePath();
        var store = new SqliteLotStore(path);
        store.Initialize();
        store.LeaveRecording("lot-x", "U1");
        var restarted = new SqliteLotStore(path);
        restarted.Initialize();
        var row = Assert.Single(restarted.Attempts("lot-x"));
        Assert.Equal("interrupted_unrecorded", row.Status);
        Assert.Equal(0, row.BytesEmitted);
        Assert.False(row.Measured);
    }

    [Fact]
    public void Official_page_source_has_no_fixture_entry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "web", "src", "App.tsx")))
        {
            dir = dir.Parent;
        }

        var text = File.ReadAllText(Path.Combine(dir!.FullName, "web", "src", "App.tsx"));
        Assert.DoesNotContain("#/dev-fixture", text, StringComparison.Ordinal);
        Assert.DoesNotContain("自动化测试夹具", text, StringComparison.Ordinal);
    }
}
