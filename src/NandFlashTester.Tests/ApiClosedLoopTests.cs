using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NandFlashTester.Core.Recipes;
using NandFlashTester.Core.Scope;
using NandFlashTester.Tests.Support;

namespace NandFlashTester.Tests;

[CollectionDefinition("api", DisableParallelization = true)]
public sealed class ApiCollection;

[Collection("api")]
public class ApiClosedLoopTests
{
    [Fact]
    public async Task Success_persists_snapshot_and_csv_matches_sqlite()
    {
        var database = TestApp.NewDatabasePath();
        await using var app = new TestApp(database);
        using var client = app.CreateClient();

        var created = await PostJobAsync(client, RecipePresets.SuccessJson);
        Assert.Equal(201, created.Status);
        var jobId = created.Body.RootElement.GetProperty("jobId").GetString()!;
        using var job = await WaitAsync(client, jobId);

        Assert.Equal("succeeded", job.RootElement.GetProperty("status").GetString());
        Assert.Equal("成功", job.RootElement.GetProperty("statusText").GetString());
        Assert.Equal(0, job.RootElement.GetProperty("errorBitCount").GetInt32());
        Assert.Equal(42, job.RootElement.GetProperty("seed").GetInt64());
        Assert.Equal("simulation", job.RootElement.GetProperty("deviceKind").GetString());
        Assert.False(job.RootElement.GetProperty("deviceSubstituted").GetBoolean());
        Assert.Contains("模拟", job.RootElement.GetProperty("resultNature").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("MT/s", job.RootElement.ToString(), StringComparison.Ordinal);

        var compare = Step(job.RootElement, "compare");
        Assert.Equal("succeeded", compare.GetProperty("status").GetString());
        Assert.Equal(256, compare.GetProperty("detail").GetProperty("totalBytes").GetInt32());
        Assert.Equal(256, compare.GetProperty("detail").GetProperty("matchedBytes").GetInt32());

        var csv = await client.GetStringAsync($"/api/jobs/{jobId}/export.csv");
        var rows = ParseCsv(csv);
        Assert.Equal(5, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(jobId, row["任务编号"]);
            Assert.Equal("成功", row["任务状态"]);
            Assert.Equal("0", row["错误位数"]);
            Assert.Equal("42", row["种子"]);
            Assert.Equal(job.RootElement.GetProperty("recipeSha256").GetString(), row["配方哈希"]);
            Assert.Equal(job.RootElement.GetProperty("resultNature").GetString(), row["结果性质"]);
            Assert.Equal("模拟", row["设备类型"]);
            Assert.Equal("否", row["未知硬件已替换"]);
            Assert.Equal("通过", row["结论"]);
        });
        Assert.Contains(rows, row => row["步骤名称"] == "比较" && row["步骤状态"] == "成功" && row["步骤覆盖"] == "已测" && row["页"] == "");
        var overview = job.RootElement.GetProperty("overview");
        Assert.Equal("通过", overview.GetProperty("conclusion").GetString());
        Assert.Equal("✓", overview.GetProperty("conclusionSymbol").GetString());
        Assert.Equal("模拟", overview.GetProperty("simulationLabel").GetString());
        Assert.Equal(0, overview.GetProperty("errorBitCount").GetInt32());
        Assert.Equal(256, overview.GetProperty("matchedBytes").GetInt32());
        Assert.Equal(256, overview.GetProperty("totalBytes").GetInt32());
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("eta").ValueKind);
        Assert.Contains("不估算剩余时间", overview.GetProperty("etaNote").GetString(), StringComparison.Ordinal);
        Assert.Contains(overview.GetProperty("unimplemented").EnumerateArray(), item => item.GetString()!.Contains("未实现", StringComparison.Ordinal));
        Assert.DoesNotContain("Stack", overview.GetProperty("reason").GetString(), StringComparison.Ordinal);

        await using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, seed, recipe_snapshot, recipe_sha256, error_bit_count, result_nature, device_kind
            FROM jobs WHERE job_id = $id
            """;
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("succeeded", reader.GetString(0));
        Assert.Equal(42, reader.GetInt64(1));
        Assert.Equal(job.RootElement.GetProperty("recipeSnapshot").GetString(), reader.GetString(2));
        Assert.Equal(rows[0]["配方哈希"], reader.GetString(3));
        Assert.Equal(0, reader.GetInt64(4));
        Assert.Equal(rows[0]["结果性质"], reader.GetString(5));
        Assert.Equal("simulation", reader.GetString(6));
    }

    [Fact]
    public async Task Injected_failure_is_located_in_the_api_csv_and_sqlite()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath());
        using var client = app.CreateClient();
        var created = await PostJobAsync(client, RecipePresets.BitFlipJson);
        var jobId = created.Body.RootElement.GetProperty("jobId").GetString()!;
        using var job = await WaitAsync(client, jobId);

        Assert.Equal("failed", job.RootElement.GetProperty("status").GetString());
        Assert.Equal("失败", job.RootElement.GetProperty("statusText").GetString());
        Assert.Equal(1, job.RootElement.GetProperty("errorBitCount").GetInt32());
        Assert.Equal(1, job.RootElement.GetProperty("errorByteCount").GetInt32());
        var error = Assert.Single(job.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(1, error.GetProperty("page").GetInt32());
        Assert.Equal(3, error.GetProperty("byteOffset").GetInt32());
        Assert.Equal(0, error.GetProperty("bit").GetInt32());
        Assert.Equal("4A", error.GetProperty("expectedHex").GetString());
        Assert.Equal("4B", error.GetProperty("actualHex").GetString());
        Assert.Contains("模拟注入", Step(job.RootElement, "compare").GetProperty("message").GetString(), StringComparison.Ordinal);
        var overview = job.RootElement.GetProperty("overview");
        Assert.Equal("失败", overview.GetProperty("conclusion").GetString());
        Assert.Equal("✕", overview.GetProperty("conclusionSymbol").GetString());
        Assert.Equal(1, overview.GetProperty("errorBitCount").GetInt32());
        Assert.Equal(job.RootElement.GetProperty("errors").GetArrayLength(), overview.GetProperty("errorBitCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("eta").ValueKind);
        Assert.Contains("错误位置", overview.GetProperty("nextStep").GetString(), StringComparison.Ordinal);

        var rows = ParseCsv(await client.GetStringAsync($"/api/jobs/{jobId}/export.csv"));
        var located = rows.Single(row => row["页"] == "1");
        Assert.Equal("3", located["字节偏移"]);
        Assert.Equal("0", located["位"]);
        Assert.Equal("4A", located["期望值"]);
        Assert.Equal("4B", located["实际值"]);
        Assert.Equal("失败", located["任务状态"]);
        Assert.Equal("失败", located["结论"]);
        Assert.Equal("1", located["错误位数"]);
        Assert.Equal("已测", located["步骤覆盖"]);
        Assert.Equal("比较", located["步骤名称"]);
        Assert.Equal(jobId, located["任务编号"]);

        await using var connection = Open(app.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT page, byte_offset, bit_index, expected_hex, actual_hex FROM job_errors WHERE job_id = $id";
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(3, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
        Assert.Equal("4A", reader.GetString(3));
        Assert.Equal("4B", reader.GetString(4));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task Cancel_stops_further_writes_and_stays_cancelled()
    {
        var gate = new GatePacing("program", 1);
        await using var app = new TestApp(TestApp.NewDatabasePath(), gate);
        using var client = app.CreateClient();
        var created = await PostJobAsync(client, RecipePresets.SuccessJson);
        var jobId = created.Body.RootElement.GetProperty("jobId").GetString()!;
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelling = await client.PostAsync($"/api/jobs/{jobId}/cancel", new StringContent(""));
        Assert.Equal(200, (int)cancelling.StatusCode);
        gate.Release();

        using var job = await WaitAsync(client, jobId);
        Assert.Equal("cancelled", job.RootElement.GetProperty("status").GetString());
        Assert.Equal("已取消", job.RootElement.GetProperty("statusText").GetString());
        var program = Step(job.RootElement, "program");
        Assert.Equal("cancelled", program.GetProperty("status").GetString());
        Assert.Equal(1, program.GetProperty("detail").GetProperty("pagesProgrammed").GetInt32());
        Assert.Equal(4, program.GetProperty("detail").GetProperty("pageCount").GetInt32());
        Assert.Equal("skipped", Step(job.RootElement, "read").GetProperty("status").GetString());
        Assert.Equal("skipped", Step(job.RootElement, "compare").GetProperty("status").GetString());

        var again = await client.PostAsync($"/api/jobs/{jobId}/cancel", new StringContent(""));
        using var againJson = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        Assert.Equal("cancelled", againJson.RootElement.GetProperty("status").GetString());
        for (var i = 0; i < 5; i++)
        {
            using var polled = JsonDocument.Parse(await (await client.GetAsync($"/api/jobs/{jobId}")).Content.ReadAsStringAsync());
            Assert.Equal("cancelled", polled.RootElement.GetProperty("status").GetString());
        }

        var csv = ParseCsv(await client.GetStringAsync($"/api/jobs/{jobId}/export.csv"));
        Assert.Contains(csv, row => row["步骤名称"] == "图案写入" && row["步骤状态"] == "已取消" && row["任务状态"] == "已取消" && row["结论"] == "已取消");
        Assert.Contains(csv, row => row["步骤名称"] == "读回" && row["步骤状态"] == "未执行" && row["步骤覆盖"] == "未测");
        var overview = job.RootElement.GetProperty("overview");
        Assert.Equal("已取消", overview.GetProperty("conclusion").GetString());
        Assert.Equal(1, overview.GetProperty("pagesProgrammed").GetInt32());
        Assert.Equal(4, overview.GetProperty("pageCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("eta").ValueKind);
        Assert.Contains("不再写入", overview.GetProperty("cancelFeedback").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Busy_device_rejects_a_second_job_until_the_first_finishes()
    {
        var gate = new GatePacing("program", 0);
        await using var app = new TestApp(TestApp.NewDatabasePath(), gate);
        using var client = app.CreateClient();
        var first = await PostJobAsync(client, RecipePresets.SuccessJson);
        var firstId = first.Body.RootElement.GetProperty("jobId").GetString()!;
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await PostJobAsync(client, RecipePresets.BitFlipJson);
        var third = await PostJobAsync(client, RecipePresets.BitFlipJson);
        Assert.Equal(409, second.Status);
        Assert.Equal(409, third.Status);
        Assert.False(second.Body.RootElement.GetProperty("executed").GetBoolean());
        Assert.Equal(firstId, second.Body.RootElement.GetProperty("jobId").GetString());
        Assert.Contains("已拒绝并发", second.Body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);

        gate.Release();
        using var done = await WaitAsync(client, firstId);
        Assert.Equal("succeeded", done.RootElement.GetProperty("status").GetString());

        var after = await PostJobAsync(client, RecipePresets.BitFlipJson);
        Assert.Equal(201, after.Status);
        using var failed = await WaitAsync(client, after.Body.RootElement.GetProperty("jobId").GetString()!);
        Assert.Equal("failed", failed.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, failed.RootElement.GetProperty("errorBitCount").GetInt32());
    }

    [Fact]
    public async Task Illegal_recipe_does_not_create_a_job()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath());
        using var client = app.CreateClient();
        const string script = """{"schema":"nand.sim.recipe/1","name":"恶意","script":"Process.Start(\"calc.exe\")","shell":"bash -c \\"rm -rf /\\""}""";
        var response = await client.PostAsync("/api/jobs", new StringContent(script, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Contains("未执行", text, StringComparison.Ordinal);
        Assert.Contains("\"executed\":false", text, StringComparison.Ordinal);
        Assert.Contains("脚本", text, StringComparison.Ordinal);

        using var list = JsonDocument.Parse(await (await client.GetAsync("/api/jobs")).Content.ReadAsStringAsync());
        Assert.Empty(list.RootElement.GetProperty("jobs").EnumerateArray());

        var validate = await client.PostAsync("/api/recipes/validate", new StringContent(script, Encoding.UTF8, "application/json"));
        Assert.Equal(400, (int)validate.StatusCode);
        using var after = JsonDocument.Parse(await (await client.GetAsync("/api/jobs")).Content.ReadAsStringAsync());
        Assert.Empty(after.RootElement.GetProperty("jobs").EnumerateArray());
    }

    [Fact]
    public async Task Unknown_hardware_is_an_explicit_simulation_stand_in()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath());
        using var client = app.CreateClient();
        var unsafeId = await PostJobAsync(client, RecipePresets.SuccessJson, "=cmd");
        Assert.Equal(201, unsafeId.Status);
        Assert.DoesNotContain("=cmd", unsafeId.Raw, StringComparison.Ordinal);
        Assert.True(unsafeId.Body.RootElement.GetProperty("deviceSubstituted").GetBoolean());
        Assert.Equal("sim-dut-1", unsafeId.Body.RootElement.GetProperty("deviceId").GetString());

        using var unsafeJob = await WaitAsync(client, unsafeId.Body.RootElement.GetProperty("jobId").GetString()!);
        Assert.Equal("invalid-device-id", unsafeJob.RootElement.GetProperty("requestedDeviceId").GetString());
        Assert.Equal("sim-dut-1", unsafeJob.RootElement.GetProperty("deviceId").GetString());
        Assert.DoesNotContain("=cmd", unsafeJob.RootElement.ToString(), StringComparison.Ordinal);

        var named = await PostJobAsync(client, RecipePresets.SuccessJson, "board-9");
        using var namedJob = await WaitAsync(client, named.Body.RootElement.GetProperty("jobId").GetString()!);
        Assert.Equal("succeeded", namedJob.RootElement.GetProperty("status").GetString());
        Assert.True(namedJob.RootElement.GetProperty("deviceSubstituted").GetBoolean());
        Assert.Equal("board-9", namedJob.RootElement.GetProperty("requestedDeviceId").GetString());
        Assert.Equal("sim-dut-1", namedJob.RootElement.GetProperty("deviceId").GetString());
        Assert.Contains("未知硬件", namedJob.RootElement.GetProperty("substitutionReason").GetString(), StringComparison.Ordinal);
        var csv = ParseCsv(await client.GetStringAsync($"/api/jobs/{named.Body.RootElement.GetProperty("jobId").GetString()}/export.csv"));
        Assert.All(csv, row =>
        {
            Assert.Equal("是", row["未知硬件已替换"]);
            Assert.Equal("board-9", row["请求设备"]);
            Assert.Equal("sim-dut-1", row["实际设备"]);
            Assert.Equal("模拟", row["设备类型"]);
        });
    }

    [Fact]
    public async Task Restart_keeps_history_and_releases_an_interrupted_job()
    {
        var database = TestApp.NewDatabasePath();
        string successId;
        await using (var app = new TestApp(database))
        {
            using var client = app.CreateClient();
            var created = await PostJobAsync(client, RecipePresets.SuccessJson);
            successId = created.Body.RootElement.GetProperty("jobId").GetString()!;
            using var done = await WaitAsync(client, successId);
            Assert.Equal("succeeded", done.RootElement.GetProperty("status").GetString());
        }

        await using (var app = new TestApp(database))
        {
            using var client = app.CreateClient();
            using var job = JsonDocument.Parse(await (await client.GetAsync($"/api/jobs/{successId}")).Content.ReadAsStringAsync());
            Assert.Equal("succeeded", job.RootElement.GetProperty("status").GetString());
            Assert.Equal(42, job.RootElement.GetProperty("seed").GetInt64());
            Assert.Equal(RecipePresets.Success.Sha256, job.RootElement.GetProperty("recipeSha256").GetString());
            Assert.Equal(5, job.RootElement.GetProperty("steps").GetArrayLength());
            using var list = JsonDocument.Parse(await (await client.GetAsync("/api/jobs")).Content.ReadAsStringAsync());
            Assert.Contains(list.RootElement.GetProperty("jobs").EnumerateArray(), item => item.GetProperty("jobId").GetString() == successId);
        }

        InsertRunningJob(database, "job-stale");
        await using (var app = new TestApp(database))
        {
            using var client = app.CreateClient();
            using var stale = JsonDocument.Parse(await (await client.GetAsync("/api/jobs/job-stale")).Content.ReadAsStringAsync());
            Assert.Equal("interrupted", stale.RootElement.GetProperty("status").GetString());
            Assert.Equal("已中断", stale.RootElement.GetProperty("statusText").GetString());
            var created = await PostJobAsync(client, RecipePresets.SuccessJson);
            Assert.Equal(201, created.Status);
            using var resumed = await WaitAsync(client, created.Body.RootElement.GetProperty("jobId").GetString()!);
            Assert.Equal("succeeded", resumed.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Cancelling_a_finished_job_does_not_change_success()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath());
        using var client = app.CreateClient();
        var created = await PostJobAsync(client, RecipePresets.SuccessJson);
        var jobId = created.Body.RootElement.GetProperty("jobId").GetString()!;
        using var done = await WaitAsync(client, jobId);
        Assert.Equal("succeeded", done.RootElement.GetProperty("status").GetString());
        var cancel = await client.PostAsync($"/api/jobs/{jobId}/cancel", new StringContent(""));
        using var body = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
        Assert.Equal("succeeded", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Meta_keeps_the_full_phase_scope_and_does_not_claim_the_rest()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath());
        using var client = app.CreateClient();
        using var meta = JsonDocument.Parse(await (await client.GetAsync("/api/meta")).Content.ReadAsStringAsync());
        Assert.Equal(36, meta.RootElement.GetProperty("itemCount").GetInt32());
        Assert.Equal(6, meta.RootElement.GetProperty("implementedSubsetCount").GetInt32());
        Assert.True(meta.RootElement.GetProperty("simulationOnly").GetBoolean());
        Assert.False(meta.RootElement.GetProperty("claimsMeasuredThroughput").GetBoolean());
        Assert.False(meta.RootElement.GetProperty("localDeliveryPathWritten").GetBoolean());
        Assert.False(meta.RootElement.GetProperty("jsonEntryIsSslCompatible").GetBoolean());
        Assert.Contains("独立重写", meta.RootElement.GetProperty("reimplementationPolicy").GetString(), StringComparison.Ordinal);
        Assert.Contains("同一测试页", meta.RootElement.GetProperty("expertEntryLocation").GetString(), StringComparison.Ordinal);
        Assert.Contains("不是 SSL", meta.RootElement.GetProperty("expertEntryLocation").GetString(), StringComparison.Ordinal);
        Assert.Equal(PhaseScope.Items.Count, meta.RootElement.GetProperty("items").GetArrayLength());
        var items = meta.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, item => item.GetProperty("id").GetString() == "F14" && item.GetProperty("status").GetString()!.StartsWith("未实现", StringComparison.Ordinal));
        Assert.Contains(items, item => item.GetProperty("id").GetString() == "F16" && item.GetProperty("status").GetString()!.Contains("未实现", StringComparison.Ordinal));
        Assert.Contains(items, item => item.GetProperty("id").GetString() == "F24" && item.GetProperty("status").GetString()!.Contains("不是 FTL", StringComparison.Ordinal));
        Assert.Contains(items, item => item.GetProperty("id").GetString() == "F29" && item.GetProperty("status").GetString()!.StartsWith("未实现", StringComparison.Ordinal));
        Assert.Equal(6, items.Count(item => item.GetProperty("status").GetString()!.StartsWith("已实现子集", StringComparison.Ordinal)));
        Assert.DoesNotContain(items, item => item.GetProperty("status").GetString()!.Contains("已验收", StringComparison.Ordinal));
    }

    private static async Task<Posted> PostJobAsync(HttpClient client, string json, string? deviceId = null)
    {
        var uri = string.IsNullOrEmpty(deviceId) ? "/api/jobs" : "/api/jobs?deviceId=" + Uri.EscapeDataString(deviceId);
        var response = await client.PostAsync(uri, new StringContent(json, Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        return new Posted((int)response.StatusCode, JsonDocument.Parse(raw), raw);
    }

    private static async Task<JsonDocument> WaitAsync(HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/jobs/{jobId}");
            var body = await response.Content.ReadAsStringAsync();
            var document = JsonDocument.Parse(body);
            var status = document.RootElement.GetProperty("status").GetString();
            if (status is "succeeded" or "failed" or "cancelled" or "interrupted")
            {
                return document;
            }

            document.Dispose();
            await Task.Delay(20);
        }

        throw new TimeoutException("任务没有在 8 秒内结束：" + jobId);
    }

    private static JsonElement Step(JsonElement job, string name) =>
        job.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("name").GetString() == name);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    private static void InsertRunningJob(string path, string jobId)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (
                job_id, recipe_name, recipe_version, recipe_schema, recipe_sha256, recipe_snapshot,
                seed, requested_device_id, device_id, device_kind, device_substituted, device_snapshot,
                status, cancel_requested, error_bit_count, error_byte_count, result_nature, created_at
            ) VALUES (
                $id, '残留任务', '1.0.0', 'nand.sim.recipe/1', 'abc', '{}',
                1, 'sim-dut-1', 'sim-dut-1', 'simulation', 0, '{}',
                'running', 0, 0, 0, '模拟，非实机性能或量产结论', $created
            )
            """;
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static List<Dictionary<string, string>> ParseCsv(string csv)
    {
        if (csv.Length > 0 && csv[0] == '\uFEFF')
        {
            csv = csv[1..];
        }

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var headers = SplitCsv(lines[0]);
        var rows = new List<Dictionary<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = SplitCsv(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var column = 0; column < headers.Count; column++)
            {
                row[headers[column]] = column < cells.Count ? cells[column] : "";
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }

    private sealed record Posted(int Status, JsonDocument Body, string Raw) : IDisposable
    {
        public void Dispose() => Body.Dispose();
    }
}
