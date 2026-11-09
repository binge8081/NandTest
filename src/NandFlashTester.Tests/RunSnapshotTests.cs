using System.Net.Http.Json;
using System.Text.Json;
using NandFlashTester.Api.Data;
using NandFlashTester.Core.Platform;
using NandFlashTester.Tests.Support;

namespace NandFlashTester.Tests;

public class RunSnapshotTests
{
    [Fact]
    public void Host_crash_marks_uncommitted_snapshot_without_hardware_recovery()
    {
        var path = TestApp.NewDatabasePath();
        var store = new SqliteScriptStore(path);
        store.Initialize();
        store.LeavePreparingForCrash("scr-1", "ABC");
        var restarted = new SqliteScriptStore(path);
        restarted.Initialize();
        var row = Assert.Single(restarted.Snapshots());
        Assert.Equal("interrupted_before_send", row.Status);
        Assert.Equal(0, row.BytesEmitted);
        Assert.False(row.HardwareRecovery);
        Assert.Contains("不是硬件断点续传", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_snapshot_links_pattern_and_survives_restart()
    {
        var database = TestApp.NewDatabasePath();
        string scriptId;
        string sha;
        await using (var app = new TestApp(database, platformMode: "Production"))
        {
            using var client = app.CreateClient();
            var payload = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
            var upload = await client.PostAsync("/api/patterns?name=synth-page&version=v2&pageSize=16", new ByteArrayContent(payload));
            using var uploaded = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
            var patternId = uploaded.RootElement.GetProperty("id").GetString()!;
            sha = uploaded.RootElement.GetProperty("sha256").GetString()!;
            Assert.False(uploaded.RootElement.GetProperty("deviceGeometryEnabled").GetBoolean());
            Assert.False(uploaded.RootElement.GetProperty("hostQuotaIsDeviceSpecification").GetBoolean());

            var created = await client.PostAsJsonAsync("/api/scripts", new { body = $"SETBUF {patternId}\nPGPROG 00 0001 0000 BUFFER 000\n" });
            scriptId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("script").GetProperty("scriptId").GetString()!;
            var planned = await client.PostAsync($"/api/scripts/{scriptId}/plan", new StringContent(""));
            using var plan = JsonDocument.Parse(await planned.Content.ReadAsStringAsync());
            Assert.Equal("planned_not_sent", plan.RootElement.GetProperty("runSnapshot").GetProperty("status").GetString());
            Assert.False(plan.RootElement.GetProperty("runSnapshot").GetProperty("hardwareRecovery").GetBoolean());
            Assert.Equal(0, plan.RootElement.GetProperty("runSnapshot").GetProperty("bytesEmitted").GetInt32());
            Assert.Contains(sha, plan.RootElement.GetProperty("runSnapshot").GetProperty("patternShas").GetString(), StringComparison.Ordinal);
            var csv = await (await client.GetAsync($"/api/scripts/{scriptId}/export.csv")).Content.ReadAsStringAsync();
            Assert.Contains(sha, csv, StringComparison.Ordinal);
            Assert.Contains("planned_not_sent", csv, StringComparison.Ordinal);
            Assert.Contains("不是硬件恢复", csv, StringComparison.Ordinal);
        }

        await using (var app = new TestApp(database, platformMode: "Production"))
        {
            using var client = app.CreateClient();
            using var listed = JsonDocument.Parse(await (await client.GetAsync("/api/run-snapshots")).Content.ReadAsStringAsync());
            Assert.False(listed.RootElement.GetProperty("hardwareRecovery").GetBoolean());
            var row = listed.RootElement.GetProperty("snapshots").EnumerateArray().Single();
            Assert.Equal(scriptId, row.GetProperty("scriptId").GetString());
            Assert.Equal("planned_not_sent", row.GetProperty("status").GetString());
            Assert.Contains(sha, row.GetProperty("patternShas").GetString(), StringComparison.Ordinal);
            Assert.False(row.GetProperty("hardwareRecovery").GetBoolean());
        }
    }

    [Fact]
    public async Task Measured_label_and_unapproved_policy_do_not_grant_results()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath(), platformMode: "Production");
        using var client = app.CreateClient();
        var analysis = await client.PostAsJsonAsync("/api/analysis/raw-bit-error", new { source = "measured", comparedBits = 1000, errorBits = 8, evidenceId = "claimed" });
        using var body = JsonDocument.Parse(await analysis.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("measured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("rawRate").ValueKind);
        Assert.Equal("未统计", body.RootElement.GetProperty("uber").GetString());

        foreach (var domain in new[] { "role", "mes", "grade" })
        {
            var policy = await client.PostAsJsonAsync("/api/policy/evaluate", new { domain, name = "synthetic" });
            using var decision = JsonDocument.Parse(await policy.Content.ReadAsStringAsync());
            Assert.False(decision.RootElement.GetProperty("approved").GetBoolean());
            Assert.False(decision.RootElement.GetProperty("applied").GetBoolean());
        }
    }

    [Fact]
    public void Windows_launcher_refuses_a_second_instance_without_touching_a_machine()
    {
        var missing = HostInstanceGate.Decide(false, false, false);
        Assert.False(missing.CanStart);
        Assert.Equal(HostInstanceGate.MissingExecutable, missing.Message);
        var busy = HostInstanceGate.Decide(true, true, false);
        var running = HostInstanceGate.Decide(true, false, true);
        Assert.Equal(HostInstanceGate.AlreadyRunning, busy.Message);
        Assert.Equal(HostInstanceGate.AlreadyRunning, running.Message);
        Assert.True(HostInstanceGate.Decide(true, false, false).CanStart);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "windows", "NandFlashTester.cmd")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var cmd = File.ReadAllText(Path.Combine(dir.FullName, "scripts", "windows", "NandFlashTester.cmd"));
        var stop = File.ReadAllText(Path.Combine(dir.FullName, "scripts", "windows", "stop.ps1"));
        Assert.Contains(HostInstanceGate.AlreadyRunning, cmd, StringComparison.Ordinal);
        Assert.Contains(HostInstanceGate.MissingExecutable, cmd, StringComparison.Ordinal);
        Assert.Contains("没有找到正在运行的上位机进程。", stop, StringComparison.Ordinal);
    }
}
