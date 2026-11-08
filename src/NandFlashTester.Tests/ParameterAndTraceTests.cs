using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NandFlashTester.Core.Recipes;
using NandFlashTester.Tests.Support;

namespace NandFlashTester.Tests;

public class ParameterAndTraceTests
{
    [Fact]
    public void Recipe_report_names_host_checks_and_does_not_claim_a_full_script()
    {
        var report = RecipeCapabilities.ForRecipe(RecipePresets.Success);
        Assert.Contains(report, item => item.Name == "pattern" && item.State == "HostReady");
        Assert.Contains(report, item => item.Name == "erase" && item.State == "BlockedNoDevice");
        Assert.DoesNotContain(report, item => item.Detail.Contains("完整专业脚本已实现", StringComparison.Ordinal));
        Assert.Contains(RecipeCapabilities.Language, item => item.Name == "step-debug" && item.State == "NotInLanguage");
    }

    [Fact]
    public async Task Disabled_device_import_does_not_activate_and_refusal_survives_restart()
    {
        var database = TestApp.NewDatabasePath();
        string attemptId;
        await using (var app = new TestApp(database, platformMode: "Production"))
        {
            using var client = app.CreateClient();
            var imported = await client.PostAsJsonAsync("/api/parameters/import", new
            {
                key = "voltage",
                version = "datasheet-claim-1",
                documentId = "doc",
                chapter = "3.1",
                proposedValue = "2700"
            });
            using var importBody = JsonDocument.Parse(await imported.Content.ReadAsStringAsync());
            Assert.False(importBody.RootElement.GetProperty("activated").GetBoolean());
            Assert.Equal(JsonValueKind.Null, importBody.RootElement.GetProperty("activeValue").ValueKind);
            Assert.False(importBody.RootElement.GetProperty("import").GetProperty("enabled").GetBoolean());

            using var library = JsonDocument.Parse(await (await client.GetAsync("/api/parameters")).Content.ReadAsStringAsync());
            Assert.False(library.RootElement.GetProperty("realDeviceParametersEnabled").GetBoolean());
            Assert.Contains(library.RootElement.GetProperty("active").EnumerateArray(), item =>
                item.GetProperty("key").GetString() == "wd-mass-production-cap-mt-s"
                && item.GetProperty("valueText").GetString() == "2400"
                && item.GetProperty("layer").GetString() == "platform-policy");

            var refused = await client.PostAsync("/api/runs", new StringContent(RecipePresets.SuccessJson, Encoding.UTF8, "application/json"));
            Assert.Equal(409, (int)refused.StatusCode);
            using var runBody = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.False(runBody.RootElement.GetProperty("executed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, runBody.RootElement.GetProperty("measuredErrorBits").ValueKind);
            attemptId = runBody.RootElement.GetProperty("attempt").GetProperty("attemptId").GetString()!;
            Assert.Equal("未开始", runBody.RootElement.GetProperty("attempt").GetProperty("hostTimeout").GetString());

            using var validate = JsonDocument.Parse(await (await client.PostAsync("/api/recipes/validate", new StringContent(RecipePresets.SuccessJson, Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync());
            Assert.False(validate.RootElement.GetProperty("completeProfessionalScript").GetBoolean());
            Assert.Contains(validate.RootElement.GetProperty("capabilities").EnumerateArray(), item => item.GetProperty("state").GetString() == "BlockedNoDevice");
        }

        await using (var app = new TestApp(database, platformMode: "Production"))
        {
            using var client = app.CreateClient();
            var cancel = await client.PostAsync($"/api/runs/{attemptId}/cancel", new StringContent(""));
            using var body = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
            Assert.Equal("cancelled_before_start", body.RootElement.GetProperty("attempt").GetProperty("status").GetString());
            Assert.Equal(0, body.RootElement.GetProperty("attempt").GetProperty("bytesEmitted").GetInt32());
            Assert.False(body.RootElement.GetProperty("hardwareAccepted").GetBoolean());
            var csv = await client.GetStringAsync("/api/runs/export.csv");
            Assert.Contains("领存NFA1000闪存测试仪", csv, StringComparison.Ordinal);
            Assert.Contains("cancelled_before_start", csv, StringComparison.Ordinal);
            Assert.Contains("未开始", csv, StringComparison.Ordinal);
        }
    }
}
