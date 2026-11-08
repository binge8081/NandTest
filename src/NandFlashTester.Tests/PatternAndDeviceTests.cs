using NandFlashTester.Core.Devices;
using NandFlashTester.Core.Patterns;

namespace NandFlashTester.Tests;

public class PatternAndDeviceTests
{
    [Fact]
    public void Increment_is_deterministic_and_crosses_the_page_boundary()
    {
        var first = PatternGenerator.Generate(PatternKind.Increment, 16, seed: 0, block: 0, page: 0);
        var second = PatternGenerator.Generate(PatternKind.Increment, 16, seed: 0, block: 0, page: 0);
        Assert.Equal(first, second);
        Assert.Equal((byte)15, first[15]);

        var next = PatternGenerator.Generate(PatternKind.Increment, 16, seed: 0, block: 0, page: 1);
        Assert.Equal((byte)16, next[0]);
        Assert.Equal((byte)31, next[15]);

        var seeded = PatternGenerator.Generate(PatternKind.Increment, 4, seed: 42, block: 0, page: 0);
        Assert.Equal(new byte[] { 42, 43, 44, 45 }, seeded);
    }

    [Fact]
    public void Constant_fills_the_page_and_prng_matches_the_known_vector()
    {
        var constant = PatternGenerator.Generate(PatternKind.Constant, 8, seed: 99, block: 1, page: 2, constantValue: 0xA5);
        Assert.All(constant, value => Assert.Equal(0xA5, value));

        var prng = PatternGenerator.Generate(PatternKind.Prng, 8, seed: 1, block: 0, page: 0);
        var again = PatternGenerator.Generate(PatternKind.Prng, 8, seed: 1, block: 0, page: 0);
        Assert.Equal(new byte[] { 33, 1, 197, 79, 209, 208, 26, 178 }, prng);
        Assert.Equal(prng, again);

        var otherPage = PatternGenerator.Generate(PatternKind.Prng, 8, seed: 1, block: 0, page: 1);
        Assert.NotEqual(prng, otherPage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void Zero_or_oversized_length_is_rejected(int length)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PatternGenerator.Generate(PatternKind.Increment, length, 1, 0, 0));
        Assert.Contains("零长和越界已拒绝", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthetic_identity_accepts_only_the_fixed_length()
    {
        SimIdentity.ParseId(SimIdentity.IdBytes);
        var shortId = Assert.Throws<InvalidDataException>(() => SimIdentity.ParseId(SimIdentity.IdBytes.AsSpan(0, 7)));
        Assert.Contains("已拒绝", shortId.Message, StringComparison.Ordinal);

        var topology = new SimTopology(1, 1, 1, 1, 2, 4, 64);
        var page = SimParameterPage.Build(topology);
        var parsed = SimParameterPage.Parse(page);
        Assert.Equal(SimParameterPage.Length, page.Length);
        Assert.True(parsed.GeometryEquals(topology));
        Assert.True(parsed.Simulation);

        var shortPage = Assert.Throws<InvalidDataException>(() => SimParameterPage.Parse(page.AsSpan(0, 63)));
        Assert.Contains("已拒绝", shortPage.Message, StringComparison.Ordinal);

        var badMagic = (byte[])page.Clone();
        badMagic[0] = (byte)'X';
        Assert.Contains("已拒绝", Assert.Throws<InvalidDataException>(() => SimParameterPage.Parse(badMagic)).Message, StringComparison.Ordinal);
        Assert.Equal("未测量", SimIdentity.Features["throughput"]);
        Assert.Equal("未实现", SimIdentity.Features["voltage"]);
        Assert.Equal("未实现", SimIdentity.Features["temperature"]);
    }

    [Fact]
    public void Program_requires_an_erased_page()
    {
        var device = new SimulatedNandDevice("sim-dut-1", new SimTopology(1, 1, 1, 1, 1, 2, 16));
        var data = PatternGenerator.Generate(PatternKind.Constant, 16, 1, 0, 0, 0x11);
        device.ProgramPage(0, 0, 0, 0, 0, 0, data);
        Assert.Throws<InvalidOperationException>(() => device.ProgramPage(0, 0, 0, 0, 0, 0, data));
        device.EraseBlock(0, 0, 0, 0, 0);
        device.ProgramPage(0, 0, 0, 0, 0, 0, data);
        Assert.Equal(data, device.ReadPage(0, 0, 0, 0, 0, 0));
    }
}
