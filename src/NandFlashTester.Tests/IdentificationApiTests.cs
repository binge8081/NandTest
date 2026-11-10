using System.Net.Http.Json;
using System.Text.Json;
using NandFlashTester.Tests.Support;

namespace NandFlashTester.Tests;

public class IdentificationApiTests
{
    [Fact]
    public async Task DecodeImportIdentifiesTheSyntheticCardWithoutSending()
    {
        var root = RepoRoot();
        await using var app = new TestApp(TestApp.NewDatabasePath(), settings: new Dictionary<string, string?>
        {
            ["Identification:SkuDirectory"] = Path.Combine(root, "schemas", "examples")
        });
        var client = app.CreateClient();
        var samples = await client.GetFromJsonAsync<JsonElement>("/api/identification/samples");
        var identified = samples.GetProperty("identified");
        var response = await client.PostAsJsonAsync("/api/identification/decode", new
        {
            captures = new[]
            {
                new
                {
                    label = "导入",
                    id00 = identified.GetProperty("id00").GetString(),
                    id20 = identified.GetProperty("id20").GetString(),
                    id40 = identified.GetProperty("id40").GetString(),
                    uniqueId = identified.GetProperty("uniqueId").GetString(),
                    parameterPage = identified.GetProperty("parameterPage").GetString()
                }
            }
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("hardwareExecuted").GetBoolean());
        Assert.Equal("已识别", body.GetProperty("report").GetProperty("state").GetString());
        Assert.Equal("SYN-ONFI-DEMO", body.GetProperty("report").GetProperty("partNumber").GetString());

        var run = await client.PostAsJsonAsync("/api/identification/run", new { cardId = "syn-onfi-demo", targets = new[] { "all" } });
        var runBody = await run.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(runBody.GetProperty("sent").GetBoolean());
        Assert.Equal(0, runBody.GetProperty("wireBytesEmitted").GetInt32());
        Assert.Equal("NOT_CONNECTED", runBody.GetProperty("code").GetString());
        Assert.Contains("未连接", runBody.GetProperty("message").GetString(), StringComparison.Ordinal);

        var confirmed = await client.PostAsync("/api/identification/chip/confirm", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, confirmed.StatusCode);
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/identification");
        var part = overview.GetProperty("chip").GetProperty("fields").EnumerateArray().Single(item => item.GetProperty("key").GetString() == "partNumber");
        Assert.Equal("SYN-ONFI-DEMO", part.GetProperty("value").GetString());
        var gap = overview.GetProperty("cards").EnumerateArray().Single(item => item.GetProperty("cardId").GetString() == "syn-gap");
        Assert.Contains(gap.GetProperty("pending").EnumerateArray(), item => item.GetProperty("field").GetString() == "tWHR");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NandFlashTester.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
