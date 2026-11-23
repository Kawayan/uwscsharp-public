using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWorkerWindowProtocolTests
{
    private static readonly SchedulerWorkerIdentity Identity = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly DateTimeOffset ObservedAt =
        new(2027, 1, 2, 3, 4, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task DispatchCaptureAndOrderedArgumentsSurviveFragmentedFrame()
    {
        var trigger = new SchedulerWindowTrigger(new(0x20000000000001, 123, 456), ObservedAt);
        SchedulerEntry entry = SchedulerSettingsTests.Entry(new SchedulerWindowCondition("対象", ""));
        var dispatch = new SchedulerDispatch(Identity.Generation,
            new SchedulerExecutionRequest(Identity.OccurrenceId, entry, ObservedAt, trigger));
        var request = SchedulerWorkerMessage.Request.FromDispatch(dispatch);
        Assert.Same(trigger, request.WindowTrigger);
        byte[] bytes = await Encode(request);
        using var stream = new FragmentedStream(bytes);
        SchedulerWorkerMessage.Request actual = Assert.IsType<SchedulerWorkerMessage.Request>(
            await SchedulerWorkerProtocol.ReadAsync(stream, Identity));
        Assert.Equal(trigger.Target, actual.WindowTrigger!.Target);
        Assert.Equal(ObservedAt.ToUniversalTime(), actual.WindowTrigger.ObservedAt);
        Assert.Equal(TimeSpan.Zero, actual.WindowTrigger.ObservedAt.Offset);
        Assert.Equal(request.Arguments, actual.Arguments);
        using var document = JsonDocument.Parse(bytes.AsMemory(5));
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task AbsentCaptureIsExplicitNullAndRequired()
    {
        byte[] bytes = await Encode(Request());
        JsonObject json = JsonNode.Parse(bytes.AsSpan(5))!.AsObject();
        Assert.True(json.ContainsKey("window"));
        Assert.Null(json["window"]);
        using (var stream = new MemoryStream(bytes))
        {
            SchedulerWorkerMessage.Request actual = Assert.IsType<SchedulerWorkerMessage.Request>(
                await SchedulerWorkerProtocol.ReadAsync(stream, Identity));
            Assert.Null(actual.WindowTrigger);
        }
        json.Remove("window");
        await Reject(json.ToJsonString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("\"window\"")]
    [InlineData("{\"handle\":1,\"pid\":1}")]
    [InlineData("{\"handle\":0,\"pid\":1,\"tid\":1}")]
    [InlineData("{\"handle\":1,\"pid\":0,\"tid\":1}")]
    [InlineData("{\"handle\":1,\"pid\":1,\"tid\":0}")]
    [InlineData("{\"handle\":-1,\"pid\":1,\"tid\":1}")]
    [InlineData("{\"handle\":18446744073709551616,\"pid\":1,\"tid\":1}")]
    [InlineData("{\"handle\":1,\"pid\":4294967296,\"tid\":1}")]
    [InlineData("{\"handle\":1,\"pid\":1,\"tid\":4294967296}")]
    [InlineData("{\"handle\":1.5,\"pid\":1,\"tid\":1}")]
    [InlineData("{\"handle\":\"1\",\"pid\":1,\"tid\":1}")]
    [InlineData("{\"handle\":1,\"pid\":true,\"tid\":1}")]
    [InlineData("{\"handle\":1,\"pid\":1,\"tid\":1,\"extra\":0}")]
    [InlineData("{\"handle\":1,\"pid\":1,\"pid\":1,\"tid\":1}")]
    public async Task InvalidCaptureDoesNotBecomeNoCapture(string window)
    {
        byte[] bytes = await Encode(Request());
        string json = Encoding.UTF8.GetString(bytes, 5, bytes.Length - 5);
        await Reject(json.Replace("\"window\":null", "\"window\":" + window,
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsignedIdentityBoundariesAreNotRoundedOrTruncated()
    {
        var trigger = new SchedulerWindowTrigger(new(ulong.MaxValue, uint.MaxValue, uint.MaxValue), ObservedAt);
        using var stream = new MemoryStream(await Encode(Request(trigger)));
        SchedulerWorkerMessage.Request actual = Assert.IsType<SchedulerWorkerMessage.Request>(
            await SchedulerWorkerProtocol.ReadAsync(stream, Identity));
        Assert.Equal(trigger.Target, actual.WindowTrigger!.Target);
    }

    [Fact]
    public async Task InvalidOutgoingCaptureIsRejectedBeforeFrameIsWritten()
    {
        foreach (SchedulerWindowTrigger trigger in new[]
        {
            new SchedulerWindowTrigger(default, ObservedAt),
            new SchedulerWindowTrigger(new(1, 1, 1), ObservedAt.AddSeconds(1))
        })
        {
            using var stream = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SchedulerWorkerProtocol.WriteAsync(stream, Request(trigger)));
            Assert.Equal(0, stream.Length);
        }
    }

    [Fact]
    public async Task LegacyRequestIsRejectedEvenWithoutCapture()
    {
        byte[] bytes = await Encode(Request());
        JsonObject json = JsonNode.Parse(bytes.AsSpan(5))!.AsObject();
        json["version"] = 1;
        json.Remove("window");
        await Reject(json.ToJsonString());
    }

    private static SchedulerWorkerMessage.Request Request(SchedulerWindowTrigger? trigger = null) =>
        new(Identity, Guid.NewGuid(), Path.GetFullPath("対象.uws", Path.GetTempPath()),
            ["", "a b", "日本語\n次行"], ObservedAt, trigger);

    private static async Task<byte[]> Encode(SchedulerWorkerMessage message)
    {
        using var stream = new MemoryStream();
        await SchedulerWorkerProtocol.WriteAsync(stream, message);
        return stream.ToArray();
    }

    private static async Task Reject(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[5 + payload.Length];
        frame[0] = (byte)SchedulerWorkerMessageKind.Request;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame, 5);
        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, Identity));
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
}
