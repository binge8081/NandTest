namespace NandFlashTester.Core.Devices;

public sealed record SimTopology(
    int Channels,
    int Ces,
    int Luns,
    int Planes,
    int Blocks,
    int PagesPerBlock,
    int PageSizeBytes)
{
    public long TotalPages =>
        (long)Channels * Ces * Luns * Planes * Blocks * PagesPerBlock;

    public long TotalBytes => TotalPages * PageSizeBytes;
}

/// <summary>
/// 内存中的 NAND 介质替身。擦除把整块写成 0xFF，编程要求该页处于已擦除状态。
/// 不模拟真实时序、电压、温度或量产吞吐。
/// </summary>
public sealed class SimulatedNandDevice
{
    public const byte ErasedByte = 0xFF;
    public const int MaxMediaBytes = 1_048_576;

    private readonly byte[] _media;
    private readonly byte[] _programmed;

    public SimulatedNandDevice(string deviceId, SimTopology topology)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("设备编号不能为空。", nameof(deviceId));
        }

        ArgumentNullException.ThrowIfNull(topology);
        if (topology.Channels < 1 || topology.Ces < 1 || topology.Luns < 1 || topology.Planes < 1
            || topology.Blocks < 1 || topology.PagesPerBlock < 1 || topology.PageSizeBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topology), "拓扑维度必须为正。");
        }

        if (topology.TotalBytes > MaxMediaBytes || topology.TotalPages > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(topology), "模拟介质超过 1 MiB 上限。");
        }

        DeviceId = deviceId;
        Topology = topology;
        _media = new byte[(int)topology.TotalBytes];
        Array.Fill(_media, ErasedByte);
        _programmed = new byte[(int)topology.TotalPages];
    }

    public string DeviceId { get; }

    public SimTopology Topology { get; }

    public int ProgramCalls { get; private set; }

    public byte[] ReadId() => (byte[])SimIdentity.IdBytes.Clone();

    public string ReadStatus() => "SIM_READY";

    public byte[] ReadParameterPage() => SimParameterPage.Build(Topology);

    public IReadOnlyDictionary<string, string> ReadFeatures() => SimIdentity.Features;

    public void EraseBlock(int channel, int ce, int lun, int plane, int block)
    {
        var pageIndex = Locate(channel, ce, lun, plane, block, 0);
        var byteOffset = pageIndex * Topology.PageSizeBytes;
        var byteLength = Topology.PagesPerBlock * Topology.PageSizeBytes;
        Array.Fill(_media, ErasedByte, byteOffset, byteLength);
        Array.Fill(_programmed, (byte)0, pageIndex, Topology.PagesPerBlock);
    }

    public void ProgramPage(int channel, int ce, int lun, int plane, int block, int page, ReadOnlySpan<byte> data)
    {
        if (data.Length != Topology.PageSizeBytes)
        {
            throw new ArgumentException("写入长度必须等于页大小。", nameof(data));
        }

        var pageIndex = Locate(channel, ce, lun, plane, block, page);
        if (_programmed[pageIndex] != 0)
        {
            throw new InvalidOperationException("该页尚未擦除，模拟介质拒绝再次编程。");
        }

        data.CopyTo(_media.AsSpan(pageIndex * Topology.PageSizeBytes, data.Length));
        _programmed[pageIndex] = 1;
        ProgramCalls++;
    }

    public byte[] ReadPage(int channel, int ce, int lun, int plane, int block, int page)
    {
        var pageIndex = Locate(channel, ce, lun, plane, block, page);
        var copy = new byte[Topology.PageSizeBytes];
        _media.AsSpan(pageIndex * Topology.PageSizeBytes, copy.Length).CopyTo(copy);
        return copy;
    }

    public void FlipBit(int channel, int ce, int lun, int plane, int block, int page, int byteOffset, int bit)
    {
        if (bit is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(bit));
        }

        if (byteOffset < 0 || byteOffset >= Topology.PageSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        }

        var pageIndex = Locate(channel, ce, lun, plane, block, page);
        var index = pageIndex * Topology.PageSizeBytes + byteOffset;
        _media[index] ^= (byte)(1 << bit);
    }

    private int Locate(int channel, int ce, int lun, int plane, int block, int page)
    {
        var t = Topology;
        if (channel < 0 || channel >= t.Channels
            || ce < 0 || ce >= t.Ces
            || lun < 0 || lun >= t.Luns
            || plane < 0 || plane >= t.Planes
            || block < 0 || block >= t.Blocks
            || page < 0 || page >= t.PagesPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "地址超出模拟拓扑。");
        }

        return (((((channel * t.Ces + ce) * t.Luns + lun) * t.Planes + plane) * t.Blocks + block) * t.PagesPerBlock) + page;
    }
}
