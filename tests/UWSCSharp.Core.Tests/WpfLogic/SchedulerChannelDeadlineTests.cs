using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 代替Streamと手動時計で送信の直列待機を調べ、画面や実入力を使いません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerChannelDeadlineTests
{
    private static readonly SchedulerWorkerIdentity Identity =
        new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalDeadlineIsTimeoutForQueuedAndWritingSenders(bool queued)
    {
        var clock = new ChannelClock();
        using var stream = new FirstWriteGate();
        using var channel = new SchedulerWorkerChannel(stream, Identity, clock);
        using var caller = new CancellationTokenSource();
        Task owner = channel.SendAsync(new SchedulerWorkerMessage.Start(Identity), caller.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task target = queued
            ? channel.SendAsync(new SchedulerWorkerMessage.Cancel(Identity), caller.Token)
            : owner;
        try
        {
            clock.Fire(queued ? 1 : 0);
            TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() => target);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            Assert.False(caller.IsCancellationRequested);
            Assert.Equal(0, stream.Length);
            if (queued)
            {
                Assert.False(owner.IsCompleted);
                Assert.Equal(1, stream.WriteCalls);
            }
        }
        finally
        {
            stream.Release.TrySetResult();
            // アサート失敗でも、専用の待機を解放してから通信路を破棄します。
            await Observe(owner);
        }
        Assert.True(clock.IsDisposed(queued ? 1 : 0));
        if (queued)
        {
            await channel.SendAsync(new SchedulerWorkerMessage.Start(Identity));
            await AssertFrames(stream, 2);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() =>
                channel.SendAsync(new SchedulerWorkerMessage.Start(Identity)));
            await AssertFrames(stream, 0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationRemainsCancellationAndReleasesOnlyItsLease(bool queued)
    {
        var clock = new ChannelClock();
        using var stream = new FirstWriteGate();
        using var channel = new SchedulerWorkerChannel(stream, Identity, clock);
        using var ownerCancellation = new CancellationTokenSource();
        using var waiterCancellation = new CancellationTokenSource();
        Task owner = channel.SendAsync(new SchedulerWorkerMessage.Start(Identity), ownerCancellation.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task target = queued
            ? channel.SendAsync(new SchedulerWorkerMessage.Cancel(Identity), waiterCancellation.Token)
            : owner;
        try
        {
            (queued ? waiterCancellation : ownerCancellation).Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => target);
            if (queued)
            {
                Assert.False(owner.IsCompleted);
                Assert.False(ownerCancellation.IsCancellationRequested);
            }
            Assert.Equal(0, stream.Length);
        }
        finally
        {
            stream.Release.TrySetResult();
            await Observe(owner);
        }
        Assert.True(clock.IsDisposed(queued ? 1 : 0));
        if (queued)
        {
            await channel.SendAsync(new SchedulerWorkerMessage.Start(Identity));
            await AssertFrames(stream, 2);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() =>
                channel.SendAsync(new SchedulerWorkerMessage.Start(Identity)));
            await AssertFrames(stream, 0);
        }
    }

    [Fact]
    public async Task QueuedSenderKeepsTheSameDeadlineAfterAcquiringTheWriter()
    {
        var clock = new ChannelClock();
        using var stream = new FirstWriteGate(blockSecondFrame: true);
        using var channel = new SchedulerWorkerChannel(stream, Identity, clock);
        Task owner = channel.SendAsync(new SchedulerWorkerMessage.Start(Identity));
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task queued = channel.SendAsync(new SchedulerWorkerMessage.Cancel(Identity));
        try
        {
            Assert.Equal(2, clock.Count);
            stream.Release.TrySetResult();
            await owner;
            await stream.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(clock.IsDisposed(0));
            Assert.False(clock.IsDisposed(1));
            Assert.Equal(2, clock.Count);
            clock.Fire(1);
            await Assert.ThrowsAsync<TimeoutException>(() => queued);
        }
        finally
        {
            stream.Release.TrySetResult();
            stream.SecondRelease.TrySetResult();
            await Observe(owner);
            await Observe(queued);
        }
        await AssertFrames(stream, 1);
    }

    [Fact]
    public async Task CallerCancellationWinsWhenTheInternalDeadlineAlsoExpired()
    {
        var clock = new ChannelClock();
        using var stream = new FirstWriteGate(holdFailure: true);
        using var channel = new SchedulerWorkerChannel(stream, Identity, clock);
        using var caller = new CancellationTokenSource();
        Task sending = channel.SendAsync(new SchedulerWorkerMessage.Start(Identity), caller.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            clock.Fire(0);
            caller.Cancel();
            stream.ReleaseFailure.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        }
        finally
        {
            stream.Release.TrySetResult();
            stream.ReleaseFailure.TrySetResult();
            await Observe(sending);
        }
        Assert.True(clock.IsDisposed(0));
    }

    [Fact]
    public async Task UnrelatedCancellationIsNotConvertedAndConnectionRejectsReuse()
    {
        var error = new OperationCanceledException("通信期限以外の取消例外");
        using var stream = new FirstWriteFailure(error);
        var clock = new ChannelClock();
        using var channel = new SchedulerWorkerChannel(stream, Identity, clock);
        OperationCanceledException actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            channel.SendAsync(new SchedulerWorkerMessage.Start(Identity)));
        Assert.Same(error, actual);
        Assert.True(clock.IsDisposed(0));
        await Assert.ThrowsAsync<IOException>(() =>
            channel.SendAsync(new SchedulerWorkerMessage.Start(Identity)));
        await AssertFrames(stream, 0);
    }

    private static async Task AssertFrames(MemoryStream stream, int count)
    {
        stream.Position = 0;
        for (int index = 0; index < count; index++)
        {
            Assert.IsType<SchedulerWorkerMessage.Start>(
                await SchedulerWorkerProtocol.ReadAsync(stream, Identity));
        }
        Assert.Equal(stream.Length, stream.Position);
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException) when (task.IsCompleted)
        {
        }
    }

    private sealed class FirstWriteGate(bool holdFailure = false,
        bool blockSecondFrame = false) : MemoryStream
    {
        private int writeCalls;
        internal int WriteCalls => Volatile.Read(ref writeCalls);

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource SecondRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref writeCalls);
            if (call == 1)
            {
                Entered.TrySetResult();
                try
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (holdFailure)
                {
                    await ReleaseFailure.Task;
                    throw;
                }
            }
            else if (blockSecondFrame && call == 3)
            {
                SecondEntered.TrySetResult();
                await SecondRelease.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(bytes, cancellationToken);
        }
    }

    private sealed class FirstWriteFailure(Exception error) : MemoryStream
    {
        private bool failed;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            if (!failed)
            {
                failed = true;
                return ValueTask.FromException(error);
            }
            return base.WriteAsync(bytes, cancellationToken);
        }
    }

    private sealed class ChannelClock : TimeProvider
    {
        private readonly List<ChannelTimer> timers = [];

        internal int Count => timers.Count;

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(SchedulerWorkerProtocol.CommunicationTimeout, dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ChannelTimer(callback, state);
            timers.Add(timer);
            return timer;
        }

        internal void Fire(int index) => timers[index].Fire();

        internal bool IsDisposed(int index) => timers[index].Disposed;

        private sealed class ChannelTimer(TimerCallback callback, object? state) : ITimer
        {
            private int disposed;
            internal bool Disposed => Volatile.Read(ref disposed) != 0;

            internal void Fire()
            {
                Assert.False(Disposed);
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => Interlocked.Exchange(ref disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
