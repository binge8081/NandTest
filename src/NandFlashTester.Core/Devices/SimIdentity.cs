using System.Globalization;
using System.Text;

namespace NandFlashTester.Core.Devices;

public static class SimIdentity
{
    public const string Kind = "simulation";
    public const string KindText = "模拟";
    public const string Manufacturer = "SIMULATED";
    public const string Model = "NAND-SIM-MIN-V1";
    public const string StandInId = "sim-dut-1";
    public const string Disclaimer = "模拟替身。不是真实器件，不代表吞吐、电压、温度、QLC 能力或量产结果。";
    public const string ResultNature = "模拟，非实机性能或量产结论";
    public const string Throughput = "未测量";
    public const string Voltage = "未实现";
    public const string Temperature = "未实现";

    public static readonly byte[] IdBytes = [0x53, 0x49, 0x4D, 0x01, 0x00, 0x00, 0x00, 0x01];

    public static IReadOnlyDictionary<string, string> Features { get; } = new Dictionary<string, string>
    {
        ["simulation"] = "true",
        ["throughput"] = Throughput,
        ["voltage"] = Voltage,
        ["temperature"] = Temperature,
        ["handler"] = "未实现",
        ["thermalChamber"] = "未实现"
    };

    public static void ParseId(ReadOnlySpan<byte> id)
    {
        if (id.Length != IdBytes.Length)
        {
            throw new InvalidDataException($"标识长度必须为 {IdBytes.Length}，实际 {id.Length}，已拒绝。");
        }

        if (!id.SequenceEqual(IdBytes))
        {
            throw new InvalidDataException("标识与模拟设备固定标识不一致，已拒绝。");
        }
    }

    public static string ToHex(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

public sealed record SimParameterPage(
    int PageSizeBytes,
    int PagesPerBlock,
    int Blocks,
    int Planes,
    int Luns,
    int Ces,
    int Channels,
    bool Simulation)
{
    public const int Length = 64;

    public bool GeometryEquals(SimTopology topology) =>
        PageSizeBytes == topology.PageSizeBytes
        && PagesPerBlock == topology.PagesPerBlock
        && Blocks == topology.Blocks
        && Planes == topology.Planes
        && Luns == topology.Luns
        && Ces == topology.Ces
        && Channels == topology.Channels
        && Simulation;

    public static byte[] Build(SimTopology topology)
    {
        var page = new byte[Length];
        Array.Fill(page, (byte)0xFF);
        page[0] = (byte)'S';
        page[1] = (byte)'I';
        page[2] = (byte)'M';
        page[3] = (byte)'1';
        WriteUInt16(page, 4, checked((ushort)topology.PageSizeBytes));
        WriteUInt16(page, 6, checked((ushort)topology.PagesPerBlock));
        WriteUInt16(page, 8, checked((ushort)topology.Blocks));
        page[10] = checked((byte)topology.Planes);
        page[11] = checked((byte)topology.Luns);
        page[12] = checked((byte)topology.Ces);
        page[13] = checked((byte)topology.Channels);
        page[14] = 0;
        page[15] = 0x01;
        return page;
    }

    public static SimParameterPage Parse(ReadOnlySpan<byte> page)
    {
        if (page.Length != Length)
        {
            throw new InvalidDataException($"参数页长度必须为 {Length}，实际 {page.Length}，已拒绝。");
        }

        if (page[0] != (byte)'S' || page[1] != (byte)'I' || page[2] != (byte)'M' || page[3] != (byte)'1')
        {
            throw new InvalidDataException("参数页魔数不是 SIM1，已拒绝。");
        }

        return new SimParameterPage(
            ReadUInt16(page, 4),
            ReadUInt16(page, 6),
            ReadUInt16(page, 8),
            page[10],
            page[11],
            page[12],
            page[13],
            page[15] == 0x01);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static int ReadUInt16(ReadOnlySpan<byte> buffer, int offset) =>
        buffer[offset] | (buffer[offset + 1] << 8);
}
