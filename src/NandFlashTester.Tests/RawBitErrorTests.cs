using System.Net.Http.Json;
using System.Text.Json;
using NandFlashTester.Core.Execution;
using NandFlashTester.Tests.Support;

namespace NandFlashTester.Tests;

public class RawBitErrorTests
{
    [Fact]
    public void Raw_rate_uses_only_the_supplied_denominator_and_leaves_uber_unset()
    {
        var synthetic = RawBitError.Compute("synthetic", 1000, 8);
        Assert.Equal(0.008, synthetic.RawRate);
        Assert.Equal("错误位/已比较位", synthetic.Unit);
        Assert.Equal("未统计", synthetic.Uber);
        Assert.Equal("未统计", synthetic.Ecc);
        Assert.False(synthetic.ThresholdDecided);
        Assert.False(synthetic.Measured);

        var measured = RawBitError.Compute("measured", 1000, 8);
        Assert.Equal(0.008, measured.RawRate);
        Assert.True(measured.Measured);
        Assert.Equal("未统计", measured.Uber);
        Assert.False(measured.ThresholdDecided);

        var missing = RawBitError.Compute("synthetic", 0, 1);
        Assert.Null(missing.RawRate);
        Assert.Equal("未统计", missing.Uber);

        var invented = RawBitError.Compute("guess", 1000, 1);
        Assert.Null(invented.RawRate);
        Assert.False(invented.Measured);
    }

    [Fact]
    public async Task Analysis_endpoint_does_not_emit_a_curve_or_a_pass_fail()
    {
        await using var app = new TestApp(TestApp.NewDatabasePath(), platformMode: "Production");
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/analysis/raw-bit-error", new { source = "isolated-test", comparedBits = 256, errorBits = 1 });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("错误位/已比较位", body.RootElement.GetProperty("unit").GetString());
        Assert.Equal("未统计", body.RootElement.GetProperty("uber").GetString());
        Assert.Equal("未统计", body.RootElement.GetProperty("ecc").GetString());
        Assert.False(body.RootElement.GetProperty("thresholdDecided").GetBoolean());
        Assert.False(body.RootElement.GetProperty("measured").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("curve", out _));
    }
}
