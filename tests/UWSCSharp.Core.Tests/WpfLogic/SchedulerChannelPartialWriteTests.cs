using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 代替Streamへ一部だけ書き込み、画面や実入力なしで送信失敗を検証します。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerChannelPartialWriteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialFrameFailureRejectsQueuedAndSubsequentMessages(bool payload)
    {
        var identity = new SchedulerWorkerIdentity(Guid.NewGuid(), Guid.NewGuid());
        using var stream = new PartialFailureStream(payload);
        using var channel = new SchedulerWorkerChannel(stream, identity);
        Task first = channel.SendAsync(new SchedulerWorkerMessage.Start(identity));
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task queued = channel.SendAsync(new SchedulerWorkerMessage.Heartbeat(identity));
        Assert.False(queued.IsCompleted);
        byte[] partial = stream.ToArray();
        Assert.Equal(payload ? 7 : 2, partial.Length);
        try
        {
            stream.Release.TrySetResult();
            Assert.Same(stream.Failure, await Assert.ThrowsAsync<IOException>(() => first));
            IOException rejected = await Assert.ThrowsAsync<IOException>(() =>
                queued.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(stream.Failure, rejected.InnerException);
            IOException later = await Assert.ThrowsAsync<IOException>(() =>
                channel.SendAsync(new SchedulerWorkerMessage.Start(identity)));
            Assert.Same(stream.Failure, later.InnerException);
            Assert.Equal(partial, stream.ToArray());
            Assert.Equal(payload ? 2 : 1, stream.WriteCalls);
            // 通信路の所有者が閉じる責任を持ち、Channelは勝手にStreamを破棄しません。
            Assert.True(stream.CanWrite);
        }
        finally
        {
            stream.Release.TrySetResult();
            await Observe(first);
            await Observe(queued);
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
    }

    private sealed class PartialFailureStream(bool payload) : MemoryStream
    {
        internal IOException Failure { get; } = new("部分送信後の検証用失敗");

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int WriteCalls
        {
            get;
            private set;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            if (WriteCalls == (payload ? 2 : 1))
            {
                await base.WriteAsync(bytes[..2], cancellationToken);
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                throw Failure;
            }
            await base.WriteAsync(bytes, cancellationToken);
        }
    }
}
