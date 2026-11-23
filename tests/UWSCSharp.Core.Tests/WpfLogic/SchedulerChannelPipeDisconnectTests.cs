using System.IO.Pipes;
using UWSCSharp.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 同一テストプロセス内の実パイプを切断し、画面や実入力を操作しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerChannelPipeDisconnectTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PeerDisconnectWithinFrameStopsQueuedAndLaterSends(bool payload)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string name = "uwsc-frame-disconnect-" + Guid.NewGuid().ToString("N");
        using var sender = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var peer = new NamedPipeClientStream(".", name,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(sender.WaitForConnectionAsync(lifetime.Token),
            peer.ConnectAsync(lifetime.Token));
        var identity = new SchedulerWorkerIdentity(Guid.NewGuid(), Guid.NewGuid());
        var message = new SchedulerWorkerMessage.Start(identity);
        using var expected = new MemoryStream();
        await SchedulerWorkerProtocol.WriteAsync(expected, message, lifetime.Token);
        using var split = new SplitPipeStream(sender, payload);
        using var channel = new SchedulerWorkerChannel(split, identity);
        Task first = channel.SendAsync(message, lifetime.Token);
        Task? queued = null;
        try
        {
            byte[] received = new byte[payload ? 21 : 2];
            Task receiving = peer.ReadExactlyAsync(received, lifetime.Token).AsTask();
            await split.PrefixWritten.Task.WaitAsync(lifetime.Token);
            await receiving;
            Assert.True(received.Length < expected.Length);
            Assert.Equal(expected.ToArray()[..received.Length], received);
            Assert.False(first.IsCompleted);
            queued = channel.SendAsync(new SchedulerWorkerMessage.Heartbeat(identity), lifetime.Token);
            Assert.False(queued.IsCompleted);
            peer.Dispose();
            split.Continue.TrySetResult();
            IOException original = await Assert.ThrowsAsync<IOException>(() => first);
            Assert.Same(split.PipeFailure, original);
            Assert.False(lifetime.IsCancellationRequested);
            IOException blocked = await Assert.ThrowsAsync<IOException>(() => queued);
            Assert.Same(original, blocked.InnerException);
            IOException later = await Assert.ThrowsAsync<IOException>(() =>
                channel.SendAsync(message, lifetime.Token));
            Assert.Same(original, later.InnerException);
            Assert.Equal(payload ? 3 : 2, split.PipeWriteAttempts);
            Assert.False(sender.SafePipeHandle.IsClosed);
            output.WriteLine($"payload={payload},received={received.Length},frame={expected.Length},writes={split.PipeWriteAttempts}");
            output.WriteLine($"prefix={Convert.ToHexString(received)},error={original.GetType().Name},hresult=0x{original.HResult:x8}");
        }
        finally
        {
            lifetime.Cancel();
            split.Continue.TrySetResult();
            await Observe(first);
            if (queued is not null)
            {
                await Observe(queued);
            }
        }
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class SplitPipeStream(NamedPipeServerStream pipe, bool payload) : Stream
    {
        private int frameWrite;
        internal TaskCompletionSource PrefixWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal IOException? PipeFailure
        {
            get;
            private set;
        }

        internal int PipeWriteAttempts
        {
            get;
            private set;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => pipe.Flush();

        public override Task FlushAsync(CancellationToken token) => pipe.FlushAsync(token);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            frameWrite++;
            if (frameWrite == (payload ? 2 : 1))
            {
                int prefix = payload ? 16 : 2;
                await WritePipe(bytes[..prefix], cancellationToken);
                PrefixWritten.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken);
                await WritePipe(bytes[prefix..], cancellationToken);
            }
            else
            {
                await WritePipe(bytes, cancellationToken);
            }
        }

        private async ValueTask WritePipe(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            PipeWriteAttempts++;
            try
            {
                // 分割と同期だけを加え、I/O例外そのものは実パイプから受け取ります。
                await pipe.WriteAsync(bytes, token);
            }
            catch (IOException error)
            {
                PipeFailure = error;
                throw;
            }
        }
    }
}
