using System.Threading.Channels;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// メモリ上の双方向通信と偽の再生だけを使い、OSのパイプ・音声・画面・プロセスを使用しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AudioHostRunnerTests
{
    [Fact]
    public async Task StartedAndCompletedAreSentInOrder()
    {
        using Duplex stream = await StreamWithRequest();
        int thread = 0;
        int caller = Environment.CurrentManagedThreadId;
        var backend = new Backend((_, started, _) =>
        {
            thread = Environment.CurrentManagedThreadId;
            started();
        });
        Assert.Equal(0, await Bounded(AudioHostRunner.RunAsync(stream, backend)));
        Assert.NotEqual(caller, thread);
        Assert.Equal([AudioHostMessageType.Started, AudioHostMessageType.Completed],
            (await stream.MessagesAsync()).Select(message => message.Type));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFailureIsReportedBeforeOrAfterStart(bool starts)
    {
        using Duplex stream = await StreamWithRequest();
        var backend = new Backend((_, started, _) =>
        {
            if (starts)
            {
                started();
            }

            throw new AudioPlaybackException("native failure", 42);
        });
        Assert.Equal(1, await Bounded(AudioHostRunner.RunAsync(stream, backend)));
        AudioHostMessage[] messages = await stream.MessagesAsync();
        Assert.Equal(starts ? 2 : 1, messages.Length);
        Assert.Equal(AudioHostMessageType.Failed, messages[^1].Type);
        Assert.Equal(42u, messages[^1].Failure!.NativeCode);
        Assert.Equal("native failure", messages[^1].Failure!.Message);
    }

    [Fact]
    public async Task MissingStartAndDuplicateStartAreFailures()
    {
        foreach (int count in new[] { 0, 2 })
        {
            using Duplex stream = await StreamWithRequest();
            var backend = new Backend((_, started, _) =>
            {
                for (int index = 0; index < count; index++)
                {
                    started();
                }
            });
            Assert.Equal(1, await Bounded(AudioHostRunner.RunAsync(stream, backend)));
            Assert.Equal(AudioHostMessageType.Failed, (await stream.MessagesAsync())[^1].Type);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentDisconnectOrExtraRequestDoesNotWaitForBlockedBackend(bool extraRequest)
    {
        using Duplex stream = await StreamWithRequest();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend((_, started, _) =>
        {
            started();
            entered.TrySetResult();
            release.Wait();
        });
        try
        {
            Task<int> running = AudioHostRunner.RunAsync(stream, backend);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            if (extraRequest)
            {
                await stream.RequestAsync();
            }
            else
            {
                stream.EndInput();
            }

            Assert.Equal(2, await Bounded(running));
            Assert.False(backend.Finished.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            await backend.Finished.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    [Fact]
    public async Task CleanupFailureAndNativeCodeAreRetained()
    {
        using Duplex stream = await StreamWithRequest();
        var failure = new AudioPlaybackException("primary", 9);
        failure.Data["音声の解放"] = new IOException("cleanup");
        Assert.Equal(1, await Bounded(AudioHostRunner.RunAsync(stream,
            new Backend((_, _, _) => throw failure))));
        AudioHostFailure result = (await stream.MessagesAsync()).Single().Failure!;
        Assert.Contains("primary", result.Message);
        Assert.Contains("cleanup", result.Message);
        Assert.Equal(9u, result.NativeCode);
    }

    [Fact]
    public async Task LongInvalidUtf16FailureIsBoundedAndValidOnTheWire()
    {
        using Duplex stream = await StreamWithRequest();
        string text = new string('x', 4095) + "😀" + '\ud800';
        Assert.Equal(1, await Bounded(AudioHostRunner.RunAsync(stream,
            new Backend((_, _, _) => throw new IOException(text)))));
        AudioHostMessage result = (await stream.MessagesAsync()).Single();
        Assert.True(result.Failure!.Message.Length <= 4096);
        Assert.Equal(AudioHostMessageType.Failed, result.Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyInputOrNonRequestDoesNotStartBackend(bool wrongType)
    {
        using var stream = new Duplex();
        if (wrongType)
        {
            await stream.InputAsync(new(AudioHostMessageType.Started));
        }
        else
        {
            stream.EndInput();
        }

        bool called = false;
        Assert.Equal(2, await Bounded(AudioHostRunner.RunAsync(stream,
            new Backend((_, _, _) => called = true))));
        Assert.False(called);
        Assert.Empty(await stream.MessagesAsync());
    }

    [Fact]
    public async Task CancelledStartupDoesNotStartBackend()
    {
        using var stream = new Duplex();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        bool called = false;
        Assert.Equal(2, await Bounded(AudioHostRunner.RunAsync(stream,
            new Backend((_, _, _) => called = true), stop.Token)));
        Assert.False(called);
    }

    [Fact]
    public async Task BrokenOutputDoesNotReportSuccess()
    {
        using Duplex stream = await StreamWithRequest();
        stream.FailWrites = true;
        Assert.Equal(2, await Bounded(AudioHostRunner.RunAsync(stream,
            new Backend((_, started, _) => started()))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("other")]
    [InlineData("UWSCSharp-audio-../other")]
    [InlineData("UWSCSharp-audio-0000000000000000000000000000000g")]
    public void InvalidPipeNamesAreRejected(string name)
        => Assert.False(WindowsAudioHost.ValidPipeName(name));

    [Fact]
    public void OnlyTheExpectedPipeNameFormIsAccepted()
        => Assert.True(WindowsAudioHost.ValidPipeName("UWSCSharp-audio-" + Guid.NewGuid().ToString("N")));

    private static Task<int> Bounded(Task<int> task) => task.WaitAsync(TimeSpan.FromSeconds(8));

    private static async Task<Duplex> StreamWithRequest()
    {
        var stream = new Duplex();
        await stream.RequestAsync();
        return stream;
    }

    private sealed class Backend(Action<AudioHostPlayRequest, Action, CancellationToken> play) : IAudioPlaybackBackend
    {
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Play(AudioHostPlayRequest request, Action started, CancellationToken token)
        {
            try
            {
                play(request, started, token);
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    private sealed class Duplex : Stream
    {
        private readonly Channel<byte[]> input = Channel.CreateUnbounded<byte[]>();
        private readonly MemoryStream output = new();
        private byte[]? current;
        private int offset;
        internal bool FailWrites
        {
            get; set;
        }

        internal Task RequestAsync() => InputAsync(new(AudioHostMessageType.Play,
            new(AudioPlaybackKind.Beep, "")));

        internal async Task InputAsync(AudioHostMessage message)
        {
            using var frame = new MemoryStream();
            await AudioHostProtocol.WriteAsync(frame, message);
            Assert.True(input.Writer.TryWrite(frame.ToArray()));
        }

        internal void EndInput() => input.Writer.TryComplete();

        internal async Task<AudioHostMessage[]> MessagesAsync()
        {
            using var stream = new MemoryStream(output.ToArray());
            List<AudioHostMessage> result = [];
            while (await AudioHostProtocol.ReadAsync(stream) is { } message)
            {
                result.Add(message);
            }

            return [.. result];
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (current is null || offset == current.Length)
            {
                try
                {
                    current = await input.Reader.ReadAsync(token);
                    offset = 0;
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }

            int count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (FailWrites)
            {
                throw new IOException("output broken");
            }

            output.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
