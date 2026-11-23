using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 代替Streamと手動のタイマーだけを使い、画面・実入力・実時間の10秒待機を使いません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWorkerDeadlineTests
{
    private static readonly SchedulerWorkerIdentity Identity = new(Guid.NewGuid(), Guid.NewGuid());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulFrameDisposesDeadlineAndLeavesStreamOpen(bool read)
    {
        var clock = new DeadlineClock();
        var message = new SchedulerWorkerMessage.Start(Identity);
        using var stream = new MemoryStream();
        if (read)
        {
            await SchedulerWorkerProtocol.WriteAsync(stream, message);
            stream.Position = 0;
            Assert.Equal(message, await SchedulerWorkerProtocol.ReadAsync(
                stream, Identity, waitForMessage: true, clock: clock));
        }
        else
        {
            await SchedulerWorkerProtocol.WriteAsync(stream, message, clock: clock);
            stream.Position = 0;
            Assert.Equal(message, await SchedulerWorkerProtocol.ReadAsync(stream, Identity));
        }

        Assert.True(clock.Created);
        Assert.True(clock.Disposed);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.DueTime);
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
    }

    [Theory]
    [InlineData("read-header")]
    [InlineData("read-payload")]
    [InlineData("read-rest")]
    [InlineData("write-header")]
    [InlineData("write-payload")]
    [InlineData("flush")]
    public async Task InternalDeadlineIsTimeoutWithoutCancellingCaller(string stage)
    {
        var clock = new DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var stream = new BlockingStream(stage);
        Task operation = Start(stream, stage, caller.Token, clock);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        Assert.Equal(TimeSpan.FromSeconds(10), clock.DueTime);
        clock.Fire();
        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(() => operation);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.False(caller.IsCancellationRequested);
        Assert.True(clock.Disposed);
        if (stage == "flush")
        {
            Assert.True(stream.Position > 5);
        }
        else
        {
            Assert.Equal(stage is "write-payload" or "read-payload" ? 5 : stage == "read-rest" ? 1 : 0,
                stream.Position);
        }
    }

    [Theory]
    [InlineData("read-header")]
    [InlineData("read-payload")]
    [InlineData("read-rest")]
    [InlineData("write-header")]
    [InlineData("write-payload")]
    [InlineData("flush")]
    public async Task CallerCancellationRemainsCancellation(string stage)
    {
        var clock = new DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var stream = new BlockingStream(stage);
        Task operation = Start(stream, stage, caller.Token, clock);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(caller.IsCancellationRequested);
        Assert.True(clock.Disposed);
    }

    [Theory]
    [InlineData("read-header")]
    [InlineData("write-header")]
    public async Task CallerCancellationTakesPriorityWhenBothSourcesExpire(string stage)
    {
        var clock = new DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var stream = new BlockingStream(stage, holdFailure: true);
        Task operation = Start(stream, stage, caller.Token, clock);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Fire();
        caller.Cancel();
        stream.ReleaseFailure.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(clock.Disposed);
    }

    [Fact]
    public async Task IdleFirstByteWaitHasNoFrameDeadlineButCanBeCancelled()
    {
        var clock = new DeadlineClock();
        using var caller = new CancellationTokenSource();
        using var stream = new BlockingStream("read-header");
        Task operation = SchedulerWorkerProtocol.ReadAsync(stream, Identity, caller.Token,
            waitForMessage: true, clock: clock);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(clock.Created);
        Assert.False(operation.IsCompleted);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.False(clock.Created);
    }

    [Fact]
    public async Task UnrelatedCancellationExceptionIsNotRelabelledAsTimeout()
    {
        var clock = new DeadlineClock();
        var error = new OperationCanceledException("別の取消例外");
        using var stream = new ThrowingStream(error);
        OperationCanceledException actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SchedulerWorkerProtocol.ReadAsync(stream, Identity, clock: clock));
        Assert.Same(error, actual);
        Assert.True(clock.Disposed);
    }

    private static Task Start(Stream stream, string stage, CancellationToken caller, TimeProvider clock) =>
        stage.StartsWith("read", StringComparison.Ordinal)
            ? SchedulerWorkerProtocol.ReadAsync(stream, Identity, caller, stage == "read-rest", clock)
            : SchedulerWorkerProtocol.WriteAsync(stream, new SchedulerWorkerMessage.Start(Identity), caller, clock);

    private sealed class BlockingStream(string stage, bool holdFailure = false) : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int prefix = stage == "read-payload" ? 5 : stage == "read-rest" ? 1 : 0;
            if (Position < prefix)
            {
                byte[] header = [3, 2, 0, 0, 0];
                int count = Math.Min(buffer.Length, prefix - checked((int)Position));
                header.AsMemory(checked((int)Position), count).CopyTo(buffer);
                Position += count;
                return count;
            }
            await Block(cancellationToken);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stage == "write-header" || stage == "write-payload" && Position > 0)
            {
                await Block(cancellationToken);
                return;
            }
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            stage == "flush" ? Block(cancellationToken) : base.FlushAsync(cancellationToken);

        private async Task Block(CancellationToken token)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (holdFailure)
            {
                await ReleaseFailure.Task;
                throw;
            }
        }
    }

    private sealed class ThrowingStream(Exception error) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(error);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private TimerCallback? callback;
        private object? state;
        internal bool Created => callback is not null;

        internal bool Disposed
        {
            get;
            private set;
        }

        internal TimeSpan DueTime
        {
            get;
            private set;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.False(Created);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            this.callback = callback;
            this.state = state;
            DueTime = dueTime;
            return new DeadlineTimer(this);
        }

        internal void Fire()
        {
            Assert.NotNull(callback);
            callback(state);
        }

        private sealed class DeadlineTimer(DeadlineClock clock) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => clock.Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
