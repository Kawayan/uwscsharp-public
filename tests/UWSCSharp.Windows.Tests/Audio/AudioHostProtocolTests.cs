using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// メモリ上の通信だけを検査し、音声APIや画面・入力を使用しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AudioHostProtocolTests
{
    [Theory]
    [InlineData(AudioPlaybackKind.WaveFile)]
    [InlineData(AudioPlaybackKind.MidiFile)]
    [InlineData(AudioPlaybackKind.VideoFile)]
    [InlineData(AudioPlaybackKind.SystemEvent)]
    [InlineData(AudioPlaybackKind.Beep)]
    internal async Task RoundTripRequest(AudioPlaybackKind kind)
    {
        string source = kind == AudioPlaybackKind.Beep ? "" : "音声 𠮷\"改行\n.wav";
        var message = new AudioHostMessage(AudioHostMessageType.Play,
            new(kind, source, kind == AudioPlaybackKind.WaveFile ? uint.MaxValue : null));
        using var stream = new OneByteStream();
        await AudioHostProtocol.WriteAsync(stream, message);
        stream.Position = 0;
        Assert.Equal(message, await AudioHostProtocol.ReadAsync(stream));
        Assert.Null(await AudioHostProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task PreserveNotificationBoundariesAndFailure()
    {
        AudioHostMessage[] messages =
        [
            new(AudioHostMessageType.Started),
            new(AudioHostMessageType.Completed),
            new(AudioHostMessageType.Failed, Failure: new("原因\n\0𠮷", 0x80000001))
        ];
        using var stream = new OneByteStream();
        foreach (AudioHostMessage message in messages)
        {
            await AudioHostProtocol.WriteAsync(stream, message);
        }

        stream.Position = 0;
        foreach (AudioHostMessage message in messages)
        {
            Assert.Equal(message, await AudioHostProtocol.ReadAsync(stream));
        }

        Assert.Null(await AudioHostProtocol.ReadAsync(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(AudioHostProtocol.MaximumPayloadBytes + 1)]
    public async Task RejectLengthBeforeReadingPayload(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioHostProtocol.ReadAsync(stream));
        Assert.Equal(4, stream.Position);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RejectPartialHeaderOrPayload(int length)
    {
        byte[] frame = Frame("{}");
        using var stream = new OneByteStream(frame[..length]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => AudioHostProtocol.ReadAsync(stream));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Type\":1}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Type\":1,\"Version\":2}")]
    [InlineData("{\"Type\":99,\"Version\":1}")]
    [InlineData("{\"Type\":1,\"Version\":1,\"Unknown\":0}")]
    [InlineData("{\"Type\":1,\"Version\":1,\"Type\":2}")]
    [InlineData("{\"Type\":1,\"Version\":1,\"Request\":{\"Kind\":0,\"Source\":\"a\"}}")]
    [InlineData("{\"Type\":3,\"Version\":1}")]
    [InlineData("{\"Type\":0,\"Version\":1,\"Request\":{\"Source\":\"a\"}}")]
    [InlineData("{\"Type\":0,\"Version\":1,\"Request\":{\"Kind\":0}}")]
    [InlineData("{\"Type\":0,\"Version\":1,\"Request\":{\"Kind\":0,\"Source\":\"a\",\"Source\":\"b\"}}")]
    [InlineData("{\"Type\":0,\"Version\":1,\"Request\":{\"Kind\":0,\"Source\":\"a\",\"Unknown\":0}}")]
    [InlineData("{\"Type\":3,\"Version\":1,\"Failure\":{\"Message\":\"\"}}")]
    [InlineData("{\"Type\":1,\"Version\":1}{}")]
    [InlineData("{\"Type\":0,\"Version\":1,\"Request\":{\"Kind\":0,\"Source\":\"\\ud800\"}}")]
    [InlineData("{\"Type\":1,\"Version\":1,\"\\u0054ype\":2}")]
    public async Task RejectMalformedOrAmbiguousMessage(string json)
    {
        using var stream = new MemoryStream(Frame(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioHostProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task RejectInvalidUtf8()
    {
        byte[] frame = Frame("{\"Type\":1,\"Version\":1}");
        frame[^2] = 0xff;
        using var stream = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioHostProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task ValidateBeforeWritingAnyBytes()
    {
        AudioHostMessage[] invalid =
        [
            new(AudioHostMessageType.Play),
            new(AudioHostMessageType.Play, new((AudioPlaybackKind)99, "a")),
            new(AudioHostMessageType.Play, new(AudioPlaybackKind.WaveFile, "")),
            new(AudioHostMessageType.Play, new(AudioPlaybackKind.WaveFile, "a\0b")),
            new(AudioHostMessageType.Play, new(AudioPlaybackKind.Beep, "a")),
            new(AudioHostMessageType.Play, new(AudioPlaybackKind.MidiFile, "a", 1)),
            new(AudioHostMessageType.Started, Failure: new("x")),
            new(AudioHostMessageType.Failed),
            new(AudioHostMessageType.Failed, Failure: new("")),
            new(AudioHostMessageType.Started, Version: 2)
        ];
        using var stream = new MemoryStream();
        foreach (AudioHostMessage message in invalid)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => AudioHostProtocol.WriteAsync(stream, message));
            Assert.Equal(0, stream.Length);
        }
    }

    [Fact]
    public async Task RejectUnpairedSurrogateWithoutReplacingText()
    {
        using var stream = new MemoryStream();
        var message = new AudioHostMessage(AudioHostMessageType.Play,
            new(AudioPlaybackKind.WaveFile, "\ud800"));
        await Assert.ThrowsAsync<EncoderFallbackException>(() => AudioHostProtocol.WriteAsync(stream, message));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task RejectOversizedMessageBeforeWritingHeader()
    {
        using var stream = new MemoryStream();
        var message = new AudioHostMessage(AudioHostMessageType.Play,
            new(AudioPlaybackKind.WaveFile, new string('a', AudioHostProtocol.MaximumPayloadBytes)));
        await Assert.ThrowsAsync<InvalidDataException>(() => AudioHostProtocol.WriteAsync(stream, message));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task RoundTripLongUnicodePathWithinFrameLimit()
    {
        using var stream = new MemoryStream();
        string source = new string('音', 32760) + ".wav";
        var message = new AudioHostMessage(AudioHostMessageType.Play,
            new(AudioPlaybackKind.WaveFile, source));
        await AudioHostProtocol.WriteAsync(stream, message);
        stream.Position = 0;
        Assert.Equal(message, await AudioHostProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadCanBeCancelledWhileWaitingForFirstByte()
    {
        using var stream = new WaitingStream();
        using var cancellation = new CancellationTokenSource();
        Task<AudioHostMessage?> reading = AudioHostProtocol.ReadAsync(stream, cancellation.Token);
        Assert.False(reading.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptFailureBeforeOrAfterStart(bool started)
    {
        var conversation = new AudioHostConversation();
        if (started)
        {
            conversation.Accept(new(AudioHostMessageType.Started));
        }

        conversation.Accept(new(AudioHostMessageType.Failed, Failure: new("失敗", 1)));
        Assert.True(conversation.IsTerminal);
        conversation.CompleteRead();
        Assert.Throws<InvalidDataException>(() => conversation.Accept(new(AudioHostMessageType.Started)));
    }

    [Fact]
    public void RequireStartAndExactlyOneTerminalNotification()
    {
        var conversation = new AudioHostConversation();
        Assert.Throws<EndOfStreamException>(conversation.CompleteRead);
        Assert.Throws<InvalidDataException>(() => conversation.Accept(new(AudioHostMessageType.Completed)));
        Assert.Throws<InvalidDataException>(() => conversation.Accept(new(AudioHostMessageType.Play,
            new(AudioPlaybackKind.Beep, ""))));
        conversation.Accept(new(AudioHostMessageType.Started));
        Assert.Throws<InvalidDataException>(() => conversation.Accept(new(AudioHostMessageType.Started)));
        Assert.Throws<EndOfStreamException>(conversation.CompleteRead);
        conversation.Accept(new(AudioHostMessageType.Completed));
        conversation.CompleteRead();
        Assert.Throws<InvalidDataException>(() => conversation.Accept(new(AudioHostMessageType.Completed)));
    }

    private static byte[] Frame(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    private sealed class OneByteStream : MemoryStream
    {
        internal OneByteStream()
        {
        }

        internal OneByteStream(byte[] bytes) : base(bytes)
        {
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class WaitingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
