using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ProcessOutputReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8192)]
    [InlineData(20000)]
    public async Task ExactLimitAndEmptyOutputAreAccepted(int size)
    {
        byte[] expected = [.. Enumerable.Range(0, size).Select(i => (byte)i)];
        using var stream = new MemoryStream(expected);
        Assert.Equal(expected, await ProcessOutputReader.ReadAsync(stream, size, default));
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task OverflowDoesNotConsumeTheWholeStreamOrDisposeIt()
    {
        using var stream = new MemoryStream(new byte[100_000]);
        ProcessOutputLimitException error = await Assert.ThrowsAsync<ProcessOutputLimitException>(() =>
            ProcessOutputReader.ReadAsync(stream, 10, default));
        Assert.Equal(10, error.MaximumBytes);
        Assert.Equal(11, error.ObservedBytes);
        Assert.Equal(11, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ByteFragmentsAndMalformedTextRemainUnchanged()
    {
        byte[] expected = [.. Encoding.UTF8.GetBytes("日本語😀\r\n\0"), 0xff, 0xf0];
        using var stream = new FragmentStream(expected);
        Assert.Equal(expected, await ProcessOutputReader.ReadAsync(stream, 1000, default));
    }

    [Fact]
    public async Task InvalidLimitAndPrecancelDoNotRead()
    {
        using var stream = new MemoryStream([1, 2]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ProcessOutputReader.ReadAsync(stream, -1, default));
        Assert.Equal(0, stream.Position);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessOutputReader.ReadAsync(stream, 10, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task ZeroLimitRejectsEvenOneByte()
    {
        using var stream = new MemoryStream([1]);
        ProcessOutputLimitException error = await Assert.ThrowsAsync<ProcessOutputLimitException>(() =>
            ProcessOutputReader.ReadAsync(stream, 0, default));
        Assert.Equal(1, error.ObservedBytes);
    }

    [Fact]
    public async Task MaximumLimitDoesNotOverflowTheReadSize()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 },
            await ProcessOutputReader.ReadAsync(stream, int.MaxValue, default));
    }

    [Fact]
    public async Task DefaultLimitReportsOverflowAfterSixteenMebibytes()
    {
        using var stream = new MemoryStream(new byte[16 * 1024 * 1024 + 1]);
        ProcessOutputLimitException error = await Assert.ThrowsAsync<ProcessOutputLimitException>(() =>
            ProcessOutputReader.ReadAsync(stream));
        Assert.Equal(16 * 1024 * 1024, error.MaximumBytes);
        Assert.Equal(16 * 1024 * 1024 + 1, error.ObservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringReadAndAtEndOfStreamIsPreserved(bool atEnd)
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancellationStream(cancellation, atEnd);
        Task<byte[]> read = ProcessOutputReader.ReadAsync(stream, 100, cancellation.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ReadFailureKeepsTheOriginalErrorAndStreamOwnership()
    {
        using var stream = new FailingStream();
        IOException error = await Assert.ThrowsAsync<IOException>(() =>
            ProcessOutputReader.ReadAsync(stream, 100, default));
        Assert.Same(stream.Error, error);
        Assert.True(stream.CanRead);
    }

    private sealed class CancellationStream(CancellationTokenSource cancellation,
        bool atEnd) : MemoryStream
    {
        internal TaskCompletionSource Entered
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            if (atEnd)
            {
                cancellation.Cancel();
                return 0;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FailingStream() : MemoryStream([1, 2, 3])
    {
        private bool read;

        internal IOException Error { get; } = new("読取り失敗の検証です。");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (read)
            {
                return ValueTask.FromException<int>(Error);
            }

            read = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class FragmentStream(byte[] data) : Stream
    {
        private readonly MemoryStream inner = new(data, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, 1));

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
