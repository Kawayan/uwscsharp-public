using System.Collections.Concurrent;
using System.Threading.Channels;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputRefreshTests
{
    [Fact]
    public async Task IdleDeadlineUsesMonotonicClockAndRoundsUp()
    {
        var clock = new Clock();
        var session = new Session(clock);
        await using var owner = new SelectiveInputHookOwner(() => session, clock);
        await owner.Ready;
        Assert.Equal(100, await session.NextWaitAsync());
        clock.Advance(99.5);
        session.Wake();
        Assert.Equal(1, await session.NextWaitAsync());
        Assert.Equal(0, session.Refreshes);
        clock.Advance(0.5);
        session.Wake();
        Assert.Equal(100, await session.NextWaitAsync());
        Assert.Equal(1, session.Refreshes);
        Assert.All(session.Threads,
            thread => Assert.NotEqual(Environment.CurrentManagedThreadId, thread));
        Assert.Single(session.Threads.Distinct());
    }

    [Fact]
    public async Task WallClockChangesDoNotMoveDeadline()
    {
        var clock = new Clock();
        var session = new Session(clock);
        await using var owner = new SelectiveInputHookOwner(() => session, clock);
        Assert.Equal(100, await session.NextWaitAsync());
        foreach (int days in new[] { 5, -10 })
        {
            clock.ShiftWallClock(days);
            session.Wake();
            Assert.Equal(100, await session.NextWaitAsync());
            Assert.Equal(0, session.Refreshes);
        }

        clock.Advance(100);
        session.Wake();
        Assert.Equal(100, await session.NextWaitAsync());
        Assert.Equal(1, session.Refreshes);
        Assert.Equal(0, clock.WallReads);
    }

    [Fact]
    public async Task DelayedRefreshDoesNotAccumulateMissedPeriods()
    {
        var clock = new Clock();
        var session = new Session(clock);
        session.OnRefresh = () => clock.Advance(250);
        await using var owner = new SelectiveInputHookOwner(() => session, clock);
        Assert.Equal(100, await session.NextWaitAsync());
        clock.Advance(550);
        session.Wake();
        Assert.Equal(100, await session.NextWaitAsync());
        Assert.Equal(1, session.Refreshes);
        clock.Advance(99);
        session.Wake();
        Assert.Equal(1, await session.NextWaitAsync());
        Assert.Equal(1, session.Refreshes);
        clock.Advance(1);
        session.Wake();
        Assert.Equal(100, await session.NextWaitAsync());
        Assert.Equal(2, session.Refreshes);
    }

    [Fact]
    public async Task ManualRefreshResetsPeriodicDeadline()
    {
        var clock = new Clock();
        var session = new Session(clock);
        await using var owner = new SelectiveInputHookOwner(() => session, clock);
        Assert.Equal(100, await session.NextWaitAsync());
        clock.Advance(90);
        await owner.RefreshTargetsAsync();
        Assert.Equal(100, await session.NextWaitAsync());
        Assert.Equal(1, session.Refreshes);
        clock.Advance(99);
        session.Wake();
        Assert.Equal(1, await session.NextWaitAsync());
        Assert.Equal(1, session.Refreshes);
        clock.Advance(1);
        session.Wake();
        Assert.Equal(100, await session.NextWaitAsync());
        Assert.Equal(2, session.Refreshes);
    }

    [Fact]
    public async Task StopAcceptedBeforeDueRefreshPreventsIt()
    {
        var clock = new Clock();
        var session = new Session(clock);
        var owner = new SelectiveInputHookOwner(() => session, clock);
        Assert.Equal(100, await session.NextWaitAsync());
        clock.Advance(100);
        await owner.DisposeAsync();
        Assert.Equal(0, session.Refreshes);
        Assert.Equal(1, session.Stops);
        Assert.False(session.IsRegistered);
        Assert.False(owner.IsAlive);
    }

    [Fact]
    public async Task StopDuringRefreshRunsBeforeAnotherInputBatch()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var clock = new Clock();
        var session = new Session(clock);
        session.OnRefresh = () =>
        {
            entered.Set();
            Assert.True(release.Wait(5000));
        };
        var owner = new SelectiveInputHookOwner(() => session, clock);
        try
        {
            Assert.Equal(100, await session.NextWaitAsync());
            int pumps = session.Pumps;
            clock.Advance(100);
            session.Wake();
            Assert.True(entered.Wait(5000));
            Task stop = owner.DisposeAsync().AsTask();
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(pumps, session.Pumps);
            Assert.Equal(1, session.Refreshes);
            Assert.Equal(1, session.Stops);
            Assert.False(owner.IsAlive);
        }
        finally
        {
            release.Set();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task BusyInputDoesNotStarveRefresh()
    {
        var clock = new Clock();
        var session = new Session(clock);
        session.OnPump = () =>
        {
            clock.Advance(25);
            return session.Pumps < 9;
        };
        await using var owner = new SelectiveInputHookOwner(() => session, clock);
        Assert.Equal(75, await session.NextWaitAsync());
        Assert.Equal(2, session.Refreshes);
        Assert.Equal([1000000L, 2000000L], session.RefreshTimes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshFailureStopsAndPreservesOriginalError(bool manual)
    {
        var clock = new Clock();
        var session = new Session(clock);
        var error = new InvalidOperationException("一覧取得失敗");
        session.OnRefresh = () => throw error;
        var owner = new SelectiveInputHookOwner(() => session, clock);
        Assert.Equal(100, await session.NextWaitAsync());
        if (manual)
        {
            Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(
                () => owner.RefreshTargetsAsync()));
        }
        else
        {
            clock.Advance(100);
            session.Wake();
        }

        Assert.True(SpinWait.SpinUntil(() => !owner.IsAlive, 5000));
        Assert.Same(error, owner.Failure);
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.DisposeAsync().AsTask()));
        Assert.Equal(1, session.Stops);
        Assert.True(session.Suspended);
        Assert.False(session.IsRegistered);
        Assert.Equal(1, session.Refreshes);
    }

    [Fact]
    public async Task RefreshAndCleanupFailuresRetainOwnerUntilRetry()
    {
        var clock = new Clock();
        var session = new Session(clock) { StopFailures = 2 };
        var error = new InvalidOperationException("一覧取得失敗");
        session.OnRefresh = () => throw error;
        var owner = new SelectiveInputHookOwner(() => session, clock);
        Assert.Equal(100, await session.NextWaitAsync());
        clock.Advance(100);
        session.Wake();
        Assert.Equal(Timeout.Infinite, await session.NextWaitAsync());
        AggregateException failure = Assert.IsType<AggregateException>(owner.Failure);
        Assert.Contains(error, failure.InnerExceptions);
        Assert.Contains(failure.Flatten().InnerExceptions, e => e is ApplicationException);
        Assert.True(owner.IsAlive);
        Assert.True(session.Suspended);
        Assert.True(session.IsRegistered);
        await Assert.ThrowsAsync<AggregateException>(() => owner.DisposeAsync().AsTask());
        Assert.False(owner.IsAlive);
        Assert.False(session.IsRegistered);
        Assert.Equal(1, session.Refreshes);
        Assert.Equal(3, session.Stops);
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        private long wall;
        internal int WallReads;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref WallReads);
            return DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref wall));
        }

        internal void Advance(double milliseconds) =>
            Interlocked.Add(ref timestamp, TimeSpan.FromMilliseconds(milliseconds).Ticks);

        internal void ShiftWallClock(int days) =>
            Interlocked.Add(ref wall, TimeSpan.FromDays(days).Ticks);
    }

    private sealed class Session(Clock clock) : ISelectiveInputHookSession
    {
        private readonly Channel<int> waits = Channel.CreateUnbounded<int>();
        private AutoResetEvent? work;
        internal readonly ConcurrentQueue<int> Threads = new();
        internal readonly ConcurrentQueue<long> RefreshTimes = new();
        internal Action? OnRefresh;
        internal Func<bool>? OnPump;
        internal int Refreshes;
        internal int Pumps;
        internal int Stops;
        internal int StopFailures;
        internal bool Suspended;

        public bool IsRegistered
        {
            get; private set;
        }

        public void Start()
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            IsRegistered = true;
        }

        public bool Pump()
        {
            Pumps++;
            return OnPump?.Invoke() ?? false;
        }

        public void Wait(AutoResetEvent wake, int milliseconds)
        {
            Volatile.Write(ref work, wake);
            waits.Writer.TryWrite(milliseconds);
            // 時計の進行と通知を試験側で決め、実時間の経過から切り離します。
            wake.WaitOne();
        }

        public void Stop()
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            Suspended = true;
            Stops++;
            if (Interlocked.Decrement(ref StopFailures) >= 0)
            {
                throw new ApplicationException("解除失敗");
            }

            IsRegistered = false;
        }

        public void RefreshTargets()
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            Refreshes++;
            RefreshTimes.Enqueue(clock.GetTimestamp());
            OnRefresh?.Invoke();
        }

        internal void Wake() => Volatile.Read(ref work)!.Set();

        internal async Task<int> NextWaitAsync() =>
            await waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
