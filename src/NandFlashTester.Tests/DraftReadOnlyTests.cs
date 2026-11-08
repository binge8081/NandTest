using System.Text;
using System.Text.Json;
using NandFlashTester.Core.Management;

namespace NandFlashTester.Tests;

public class DraftReadOnlyTests
{
    [Fact]
    public void Frames_split_and_stick_without_assuming_packet_boundaries()
    {
        var first = DraftFrames.Encode(DraftMessages.HelloRequest("draft-fixture-client", "draft-hello"));
        var second = DraftFrames.Encode(DraftMessages.StatusRequest("draft-status"));
        var both = first.Concat(second).ToArray();
        Assert.True(DraftFrames.TryRead(both, out var consumed, out var json, out _));
        Assert.Equal("hello", DraftMessages.Parse(json!));
        Assert.True(DraftFrames.TryRead(both.AsSpan(consumed), out _, out var next, out _));
        Assert.Equal("status", DraftMessages.Parse(next!));
    }

    [Fact]
    public void Illegal_length_does_not_allocate_the_declared_payload()
    {
        var prefix = new byte[] { 0x00, 0x01, 0x00, 0x01 };
        Assert.False(DraftFrames.TryRead(prefix, out _, out var json, out var error));
        Assert.Null(json);
        Assert.Contains("未按声明长度分配", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_operation_is_not_turned_into_a_command()
    {
        var json = """{"version":"draft-1","kind":"request","request_id":"x","op":"start","payload":{},"idempotency_key":null}"""u8.ToArray();
        var error = Assert.Throws<InvalidOperationException>(() => DraftMessages.Parse(json));
        Assert.Contains("没有编成生产命令", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_key_and_configured_rate_are_rejected()
    {
        var duplicate = """{"version":"draft-1","version":"draft-1"}"""u8.ToArray();
        Assert.Contains("重复", Assert.Throws<InvalidOperationException>(() => DraftMessages.Parse(duplicate)).Message, StringComparison.Ordinal);

        var impersonated = """
            {"version":"draft-1","kind":"success","request_id":"s","op":"status","payload":{"boot_id":"b","revision":1,"state":"idle","session_id":null,"safe_to_start":true,"safe_to_reset":false,"rates":[{"scope_id":"s","requested_mt_s":2400,"actual_mt_s":2400,"evidence":"configured_readback","downgraded":false,"reason":"none","coverage":"covered","point_id":"p"}],"summary_uncovered":[]}}
            """u8.ToArray();
        Assert.Contains("不能带实测速率", Assert.Throws<InvalidOperationException>(() => DraftMessages.Parse(impersonated)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loopback_is_in_memory_and_live_connect_stays_off()
    {
        Assert.False(DraftReadOnlyProfile.LiveConnectAllowed);
        var status = DraftMessages.LoopbackHelloThenStatus();
        Assert.Equal("idle", status.State);
        Assert.False(status.SafeToStart);
        Assert.False(status.SafeToReset);
        Assert.Equal(0, status.RateCount);
        Assert.Null(status.SessionId);

        await using var blocked = new BlockingStream();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DraftFrames.ReadOneAsync(blocked, TimeSpan.FromMilliseconds(30), CancellationToken.None));
        Assert.Contains("超过时限", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_loopback_does_not_accept_an_endpoint()
    {
        await using var app = new Support.TestApp(Support.TestApp.NewDatabasePath(), platformMode: "Production");
        using var client = app.CreateClient();
        var ok = await client.PostAsync("/api/management/draft/loopback", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)ok.StatusCode);
        using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("liveConnectAllowed").GetBoolean());
        Assert.Equal("草案只读", body.RootElement.GetProperty("label").GetString());
        Assert.False(body.RootElement.GetProperty("hardwareAccepted").GetBoolean());
        Assert.False(body.RootElement.GetProperty("safeToStartGrantsCommand").GetBoolean());

        var refused = await client.PostAsync("/api/management/draft/loopback", new StringContent("{\"endpoint\":\"tcp://device\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(400, (int)refused.StatusCode);
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
