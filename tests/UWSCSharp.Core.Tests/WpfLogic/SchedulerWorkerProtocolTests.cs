using System.Buffers.Binary;
using System.Text;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWorkerProtocolTests
{
    private static readonly SchedulerWorkerIdentity Identity =
        new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task FragmentedFramesRoundTripWithoutChangingArguments(int kind)
    {
        SchedulerWorkerMessage message = kind switch
        {
            1 => Request(["", """日本語 "quote" \""", "😀\n"]),
            2 => new SchedulerWorkerMessage.Ready(Identity, 123),
            3 => new SchedulerWorkerMessage.Start(Identity),
            4 => new SchedulerWorkerMessage.Cancel(Identity),
            5 => new SchedulerWorkerMessage.Completed(Identity, 130, true, false, "回収待ち😀"),
            6 => new SchedulerWorkerMessage.Started(Identity),
            _ => new SchedulerWorkerMessage.Heartbeat(Identity)
        };
        byte[] bytes = await Encode(message);
        using var stream = new FragmentedStream(bytes);
        SchedulerWorkerMessage actual = await SchedulerWorkerProtocol.ReadAsync(stream, Identity);
        Assert.Equal(message.Identity, actual.Identity);
        if (message is SchedulerWorkerMessage.Request expected)
        {
            SchedulerWorkerMessage.Request request = Assert.IsType<SchedulerWorkerMessage.Request>(actual);
            Assert.Equal(expected.EntryId, request.EntryId);
            Assert.Equal(expected.Path, request.Path);
            Assert.Equal(expected.Arguments, request.Arguments);
            Assert.Equal(expected.ObservedAt, request.ObservedAt);
        }
        else
        {
            Assert.Equal(message, actual);
        }
        Assert.Equal(bytes.Length, stream.Position);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(255, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 8388609)]
    [InlineData(2, 65537)]
    public async Task InvalidHeaderIsRejectedBeforePayloadRead(int kind, int length)
    {
        byte[] frame = Header(kind, length);
        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, Identity));
        Assert.Equal(5, stream.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task TruncatedFramesAreNotCompletedMessages(int retained)
    {
        byte[] bytes = await Encode(new SchedulerWorkerMessage.Start(Identity));
        using var stream = new MemoryStream(bytes[..retained]);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, Identity));
    }

    [Theory]
    [InlineData("\"version\":1")]
    [InlineData("\"version\":2.5")]
    [InlineData("\"version\":\"2\"")]
    [InlineData("\"version\":2,\"version\":2")]
    [InlineData("\"version\":2,\"unknown\":false")]
    public async Task InvalidVersionDuplicateAndUnknownFieldsAreRejected(string version)
    {
        string json = "{" + version + $",\"generation\":\"{Identity.Generation}\","
            + $"\"occurrence\":\"{Identity.OccurrenceId}\"}}";
        using var stream = new MemoryStream(Frame(3, Encoding.UTF8.GetBytes(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, Identity));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"version\":2}")]
    public async Task WrongShapeAndMissingFieldsAreRejected(string json)
    {
        using var stream = new MemoryStream(Frame(3, Encoding.UTF8.GetBytes(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, Identity));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleGenerationAndOccurrenceCannotCrossRuns(bool generation)
    {
        byte[] bytes = await Encode(new SchedulerWorkerMessage.Start(Identity));
        var other = new SchedulerWorkerIdentity(
            generation ? Guid.NewGuid() : Identity.Generation,
            generation ? Identity.OccurrenceId : Guid.NewGuid());
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, other));
    }

    [Theory]
    [InlineData("\\ud800")]
    [InlineData("\\udc00")]
    [InlineData("\\ud800x")]
    [InlineData("\\ud800\\u0041")]
    [InlineData("\\u0000")]
    public async Task InvalidTextIsNotReplacedSilently(string text)
    {
        string json = $"{{\"version\":2,\"generation\":\"{Identity.Generation}\","
            + $"\"occurrence\":\"{Identity.OccurrenceId}\",\"exitCode\":1,"
            + "\"cancellationCompleted\":false,\"inputRecovered\":true,"
            + $"\"diagnostic\":\"{text}\"}}";
        using var stream = new MemoryStream(Frame(5, Encoding.UTF8.GetBytes(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, Identity));
    }

    [Fact]
    public async Task InvalidUtf8IsRejected()
    {
        using var stream = new MemoryStream(Frame(3, [0xc0, 0xaf]));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, Identity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactByteLimitsAreAcceptedAndNextByteIsNotWritten(bool request)
    {
        int maximum = request ? SchedulerWorkerProtocol.MaximumRequestBytes
            : SchedulerWorkerProtocol.MaximumControlBytes;
        SchedulerWorkerMessage Create(string text) => request
            ? Request([new string('a', SchedulerValidation.MaximumBytes), text])
            : new SchedulerWorkerMessage.Completed(Identity, 0, false, true, text);
        int overhead = (await Encode(Create(""))).Length - 5;
        int count = maximum - overhead;
        byte[] bytes = await Encode(Create(new string('x', count)));
        Assert.Equal(maximum + 5, bytes.Length);
        using var input = new MemoryStream(bytes);
        await SchedulerWorkerProtocol.ReadAsync(input, Identity);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SchedulerWorkerProtocol.WriteAsync(output, Create(new string('x', count + 1))));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task CancellationDoesNotWriteOrConsumeData()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var stream = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SchedulerWorkerProtocol.WriteAsync(stream,
                new SchedulerWorkerMessage.Start(Identity), cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, Identity, cancel.Token));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task ConsecutiveFramesKeepTheirBoundaries()
    {
        using var stream = new MemoryStream();
        await SchedulerWorkerProtocol.WriteAsync(stream, new SchedulerWorkerMessage.Start(Identity));
        await SchedulerWorkerProtocol.WriteAsync(stream, new SchedulerWorkerMessage.Cancel(Identity));
        stream.Position = 0;
        Assert.IsType<SchedulerWorkerMessage.Start>(
            await SchedulerWorkerProtocol.ReadAsync(stream, Identity));
        Assert.IsType<SchedulerWorkerMessage.Cancel>(
            await SchedulerWorkerProtocol.ReadAsync(stream, Identity));
        Assert.Equal(stream.Length, stream.Position);
    }

    private static SchedulerWorkerMessage.Request Request(IReadOnlyList<string> arguments) =>
        new(Identity, Guid.Parse("b09344e5-939d-4e9f-bcf7-0c33db797941"),
            Path.Combine(Path.GetTempPath(), "日本語.uws"), arguments,
            new DateTimeOffset(2026, 10, 31, 12, 34, 56, TimeSpan.FromHours(9)));

    private static async Task<byte[]> Encode(SchedulerWorkerMessage message)
    {
        using var stream = new MemoryStream();
        await SchedulerWorkerProtocol.WriteAsync(stream, message);
        return stream.ToArray();
    }

    private static byte[] Header(int kind, int length)
    {
        byte[] bytes = new byte[5];
        bytes[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), length);
        return bytes;
    }

    private static byte[] Frame(int kind, byte[] bytes) =>
        [.. Header(kind, bytes.Length), .. bytes];

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }

}
