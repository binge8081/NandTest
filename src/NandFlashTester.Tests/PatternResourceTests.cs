using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NandFlashTester.Core.Materials;
using NandFlashTester.Core.Patterns;
using NandFlashTester.Core.Recipes;
using NandFlashTester.Tests.Support;

namespace NandFlashTester.Tests;

public class PatternResourceTests
{
    [Fact]
    public void Bounds_reject_bad_length_and_keep_a_digest()
    {
        var good = PatternLibrary.Accept("synth-page", "v1", 16, Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef"));
        Assert.Null(good.Error);
        Assert.Equal(2, good.Resource!.PageCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(good.Resource.Data)), good.Resource.Sha256);
        Assert.Equal(16, PatternLibrary.Slice(good.Resource, 1).Length);

        Assert.NotNull(PatternLibrary.Accept("synth-page", "v1", 16, new byte[15]).Error);
        var largePage = PatternLibrary.Accept("synth-page", "v1", 8192, new byte[8192]);
        Assert.Null(largePage.Error);
        var geometry = DeviceGeometry.Check(null, largePage.Resource!.PageSize);
        Assert.False(geometry.RealDeviceEnabled);
        Assert.False(geometry.DevicePageKnown);
        Assert.Contains("不是", PatternLibrary.Accept("synth-page", "v1", 16, new byte[HostPatternQuota.Default.MaxBytes + 16]).Error, StringComparison.Ordinal);
        Assert.Contains("主机限额", PatternLibrary.Accept("synth-page", "v1", 16, new byte[HostPatternQuota.Default.MaxBytes + 16]).Error, StringComparison.Ordinal);
        Assert.NotNull(PatternLibrary.Accept("../secret", "v1", 16, new byte[16]).Error);
        Assert.Throws<ArgumentOutOfRangeException>(() => PatternLibrary.Slice(good.Resource, 2));
    }

    [Fact]
    public void Uploaded_resource_can_be_reused_without_reading_a_path()
    {
        var bytes = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var accepted = PatternLibrary.Accept("synth-page", "v1", 16, bytes).Resource!;
        var patterns = new Dictionary<string, PatternResource> { [accepted.Id] = accepted };
        var script = $"""
            SETBUF {accepted.Id}
            PGPROG 00 0000 0000 BUFATA
            PGPROG 00 0000 0001 BUFATA
            PGRDCP 00 0000 0000 0010 BUFFER 000
            """;
        var first = ScriptCompiler.Compile(script, patterns);
        var second = ScriptCompiler.Compile(script, patterns);
        Assert.True(first.Ok, string.Join(" ", first.Diagnostics.Select(item => item.Message)));
        Assert.Equal(first.Plan!.Actions.Single(action => action.Op == "compare").PatternResourceId, second.Plan!.Actions.Single(action => action.Op == "compare").PatternResourceId);
        Assert.Equal(0, first.Plan.Actions.First(action => action.Op == "program").PatternBufferIndex);
        Assert.Equal(1, first.Plan.Actions.Last(action => action.Op == "program").PatternBufferIndex);
        Assert.Contains(PatternLibrary.FormulaNote, SourceScript.PatternFormulaNote, StringComparison.Ordinal);

        var clean = ScriptInterpreter.Run(first.Plan, new IsolatedPageDriver(), ScriptLimits.Default, patterns: patterns);
        Assert.Equal(0, clean.ErrorBitCount);
        Assert.False(clean.Measured);
        Assert.Equal(0, clean.BytesEmitted);

        var blocked = ScriptInterpreter.Run(first.Plan, new BlockedScriptDriver(), ScriptLimits.Default, patterns: patterns);
        Assert.Equal(0, blocked.BytesEmitted);
        Assert.Equal("not_executed", blocked.ErrorClass);
        Assert.Null(blocked.ErrorBitCount);
    }

    [Fact]
    public async Task Upload_is_traced_on_the_plan_and_does_not_send()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath(), platformMode: "Production");
        using var client = app.CreateClient();
        var payload = Enumerable.Range(0, 16).Select(index => (byte)(index + 1)).ToArray();
        var upload = await client.PostAsync("/api/patterns?name=synth-page&version=v1&pageSize=16", new ByteArrayContent(payload));
        Assert.Equal(201, (int)upload.StatusCode);
        using var uploaded = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var id = uploaded.RootElement.GetProperty("id").GetString()!;
        var sha = uploaded.RootElement.GetProperty("sha256").GetString()!;
        Assert.Contains("逐字节等价尚未证明", uploaded.RootElement.GetProperty("formulaNote").GetString(), StringComparison.Ordinal);

        var script = $"SETBUF {id}\nPGPROG 00 0001 0000 BUFFER 000\n";
        var created = await client.PostAsJsonAsync("/api/scripts", new { body = script });
        var scriptId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("script").GetProperty("scriptId").GetString()!;
        var planned = await client.PostAsync($"/api/scripts/{scriptId}/plan", new StringContent(""));
        Assert.Equal(200, (int)planned.StatusCode);
        using var plan = JsonDocument.Parse(await planned.Content.ReadAsStringAsync());
        Assert.Equal(0, plan.RootElement.GetProperty("bytesEmitted").GetInt32());
        Assert.False(plan.RootElement.GetProperty("measured").GetBoolean());
        Assert.Equal("not_executed", plan.RootElement.GetProperty("errorClass").GetString());
        var snapshot = plan.RootElement.GetProperty("patternSnapshots").EnumerateArray().Single();
        Assert.Equal(id, snapshot.GetProperty("id").GetString());
        Assert.Equal("v1", snapshot.GetProperty("version").GetString());
        Assert.Equal(sha, snapshot.GetProperty("sha256").GetString());
        Assert.Equal(16, snapshot.GetProperty("pageSize").GetInt32());
        var csv = await client.GetStringAsync($"/api/scripts/{scriptId}/export.csv");
        Assert.Contains(sha, csv, StringComparison.Ordinal);
        Assert.Contains("未发送", csv, StringComparison.Ordinal);
        Assert.Contains("不是已完成的擦写读", csv, StringComparison.Ordinal);
    }
}
