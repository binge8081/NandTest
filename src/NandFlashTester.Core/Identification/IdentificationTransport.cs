namespace NandFlashTester.Core.Identification;

public sealed record WireGap(string Id, string Detail);

public static class WireProtocolGaps
{
    public static readonly IReadOnlyList<WireGap> All =
    [
        new("control-1-no-raw-nand", "独立接口契约 control-1 的 operation.submit 只覆盖逻辑擦除、编程和读取。没有 Read ID、90h、ECh 或 EDh。未映射的动作必须返回 UNSUPPORTED_OPERATION，不能自拟命令号。"),
        new("fpga-frame-unfrozen", "软硬件接口初稿里的帧头、magic 和微指令操作码仍是建议值，不是已冻结的线上格式。这些字节不会生成。"),
        new("endpoint-unknown", "端点、TLS、固件 build 和锁令牌都未给出。不打开连接，也不探测端口。"),
        new("dut-map-register", "DUT_MAP 寄存器没有冻结的读事务。LUN 只来自用户载入的映射文件。"),
        new("data-plane-unnamed", "读回字节的数据面对象还没有命名规则。不发明 data_plane_id，也不把读回字节塞进管理 JSON。")
    ];
}

public sealed record IdentificationRunResult(
    bool Connected,
    bool Sent,
    int WireBytesEmitted,
    string Code,
    string Message,
    IdentificationPlan Plan,
    IReadOnlyList<WireGap> Gaps);

public static class IdentificationTransport
{
    public static IdentificationRunResult Execute(IdentificationPlan plan, bool connected)
    {
        if (!connected)
        {
            return new IdentificationRunResult(
                false,
                false,
                0,
                "NOT_CONNECTED",
                "未连接。没有向设备发送任何字节，也没有改用测试夹具。连接之后，在线上格式定稿前，传输层仍会拒绝发送。",
                plan,
                WireProtocolGaps.All);
        }

        return new IdentificationRunResult(
            true,
            false,
            0,
            "UNSUPPORTED_OPERATION",
            "设备侧操作没有冻结的线上映射，已拒绝发送。逻辑周期保留在计划里，线上字节数为 0。",
            plan,
            WireProtocolGaps.All);
    }
}
