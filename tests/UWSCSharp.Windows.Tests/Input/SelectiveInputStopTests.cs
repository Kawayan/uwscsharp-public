using System.Collections.Concurrent;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputStopTests
{
    [Fact]
    public async Task TransientRemovalFailureRetriesOnceAndKeepsError()
    {
        using var session = new Session { StopFailures = 1 };
        var clock = new Clock();
        var owner = new SelectiveInputHookOwner(() => session, clock);
        try
        {
            await owner.Ready;
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => owner.DisposeAsync().AsTask());
            Assert.Same(session.Errors.Single(), error);
            Assert.Same(error, owner.Failure);
            Assert.Equal(2, session.Stops);
            Assert.False(session.IsRegistered);
            Assert.False(owner.IsAlive);
        }
        finally
        {
            await FinishAsync(owner, session);
        }
    }

    [Fact]
    public async Task PersistentRemovalFailureStopsAutomaticRetriesAndKeepsReferences()
    {
        using var session = new Session { StopFailures = 2 };
        var owner = new SelectiveInputHookOwner(() => session, new Clock());
        try
        {
            await owner.Ready;
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(
                () => owner.DisposeAsync().AsTask());
            Assert.Equal(session.Errors.ToArray(), error.Flatten().InnerExceptions);
            Assert.Equal(2, session.Stops);
            Assert.True(session.Suspended);
            Assert.True(session.IsRegistered);
            Assert.True(owner.IsAlive);
            Assert.Same(error, owner.Failure);
            await Assert.ThrowsAsync<AggregateException>(
                () => owner.DisposeAsync().AsTask());
            Assert.Equal(3, session.Stops);
            Assert.False(owner.IsAlive);
            Assert.False(session.IsRegistered);
        }
        finally
        {
            await FinishAsync(owner, session);
        }
    }

    [Fact]
    public async Task TimeoutRetainsInProgressStopAndLaterErrors()
    {
        using var session = new Session { StopFailures = 2 };
        session.Continue.Reset();
        var clock = new Clock();
        var owner = new SelectiveInputHookOwner(() => session, clock);
        try
        {
            await owner.Ready;
            Task stop = owner.DisposeAsync().AsTask();
            Assert.True(session.Entered.Wait(5000));
            clock.Advance(TimeSpan.FromSeconds(5));
            TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(() => stop);
            Assert.Same(timeout, owner.Failure);
            Assert.True(owner.IsAlive);
            Assert.True(session.IsRegistered);
            Assert.True(session.Suspended);

            Task later = owner.DisposeAsync().AsTask();
            Assert.False(later.IsCompleted);
            Assert.Same(later, owner.DisposeAsync().AsTask());
            Assert.Equal(1, session.Stops);
            session.Continue.Set();
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => later);
            Assert.Contains(timeout, error.Flatten().InnerExceptions);
            Assert.All(session.Errors, item => Assert.Contains(item, error.Flatten().InnerExceptions));
            Assert.Equal(1, session.Stops);
            Assert.True(owner.IsAlive);
            Assert.True(session.IsRegistered);
            await Assert.ThrowsAsync<AggregateException>(
                () => owner.DisposeAsync().AsTask());
            Assert.Equal(3, session.Stops);
            Assert.False(owner.IsAlive);
        }
        finally
        {
            await FinishAsync(owner, session);
        }
    }

    [Fact]
    public async Task RetrySharesFirstRequestsFiveSecondBudget()
    {
        using var secondEntered = new ManualResetEventSlim();
        using var session = new Session { StopFailures = 1 };
        session.Continue.Reset();
        session.OnStop = count =>
        {
            if (count == 2)
            {
                session.Continue.Reset();
                secondEntered.Set();
            }
        };
        var clock = new Clock();
        var owner = new SelectiveInputHookOwner(() => session, clock);
        try
        {
            await owner.Ready;
            Task stop = owner.DisposeAsync().AsTask();
            Assert.True(session.Entered.Wait(5000));
            clock.Advance(TimeSpan.FromMilliseconds(4999));
            Assert.False(stop.IsCompleted);
            session.Continue.Set();
            Assert.True(secondEntered.Wait(5000));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await Assert.ThrowsAsync<TimeoutException>(() => stop);
            Assert.Equal(2, session.Stops);
            session.Continue.Set();
            Assert.True(SpinWait.SpinUntil(() => !owner.IsAlive, 5000));
            AggregateException error = Assert.IsType<AggregateException>(owner.Failure);
            Assert.Contains(session.Errors.Single(), error.Flatten().InnerExceptions);
            Assert.Contains(error.Flatten().InnerExceptions, item => item is TimeoutException);
        }
        finally
        {
            await FinishAsync(owner, session);
        }
    }

    [Fact]
    public async Task ExpiredBudgetDoesNotStartAutomaticRetry()
    {
        using var session = new Session { StopFailures = 1 };
        session.Continue.Reset();
        var clock = new Clock();
        var owner = new SelectiveInputHookOwner(() => session, clock);
        try
        {
            await owner.Ready;
            Task stop = owner.DisposeAsync().AsTask();
            Assert.True(session.Entered.Wait(5000));
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => stop);
            Task later = owner.DisposeAsync().AsTask();
            session.Continue.Set();
            await Assert.ThrowsAsync<AggregateException>(() => later);
            Assert.Equal(1, session.Stops);
            Assert.True(owner.IsAlive);
            await Assert.ThrowsAsync<AggregateException>(
                () => owner.DisposeAsync().AsTask());
            Assert.Equal(2, session.Stops);
            Assert.False(owner.IsAlive);
        }
        finally
        {
            await FinishAsync(owner, session);
        }
    }

    [Fact]
    public async Task StartupQueueConsumesStopBudgetAndNeverRegistersAfterTimeout()
    {
        using var factoryEntered = new ManualResetEventSlim();
        using var create = new ManualResetEventSlim();
        using var session = new Session();
        var clock = new Clock();
        var owner = new SelectiveInputHookOwner(() =>
        {
            factoryEntered.Set();
            Assert.True(create.Wait(5000));
            return session;
        }, clock);
        try
        {
            Assert.True(factoryEntered.Wait(5000));
            Task stop = owner.DisposeAsync().AsTask();
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => stop);
            Task later = owner.DisposeAsync().AsTask();
            create.Set();
            await Assert.ThrowsAsync<TimeoutException>(() => later);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.Ready);
            Assert.Equal(0, session.Starts);
            Assert.Equal(1, session.Stops);
            Assert.False(owner.IsAlive);
            Assert.False(session.IsRegistered);
        }
        finally
        {
            create.Set();
            await FinishAsync(owner, session);
        }
    }

    [Theory]
    [InlineData("Refresh")]
    [InlineData("Pump")]
    [InlineData("Wait")]
    public async Task TimeoutKeepsLaterProcessingFailure(string operation)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var session = new Session();
        var error = new ApplicationException("期限超過後の処理失敗");
        Action fail = () =>
        {
            entered.Set();
            Assert.True(release.Wait(5000));
            throw error;
        };
        session.OnPump = operation == "Pump" ? fail : null;
        session.OnWait = operation == "Wait" ? fail : null;
        session.OnRefresh = operation == "Refresh" ? fail : null;
        var clock = new Clock();
        var owner = new SelectiveInputHookOwner(() => session, clock);
        Task? refresh = null;
        try
        {
            await owner.Ready;
            if (operation == "Refresh")
            {
                refresh = owner.RefreshTargetsAsync();
            }

            Assert.True(entered.Wait(5000));
            Task stop = owner.DisposeAsync().AsTask();
            clock.Advance(TimeSpan.FromSeconds(5));
            TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(() => stop);
            Task later = owner.DisposeAsync().AsTask();
            release.Set();
            if (refresh is not null)
            {
                Assert.Same(error, await Assert.ThrowsAsync<ApplicationException>(() => refresh));
            }

            AggregateException saved = await Assert.ThrowsAsync<AggregateException>(() => later);
            Assert.Contains(timeout, saved.Flatten().InnerExceptions);
            Assert.Contains(error, saved.Flatten().InnerExceptions);
            Assert.Same(saved, owner.Failure);
            Assert.False(owner.IsAlive);
            Assert.False(session.IsRegistered);
        }
        finally
        {
            release.Set();
            await FinishAsync(owner, session);
        }
    }

    private static async Task FinishAsync(SelectiveInputHookOwner owner, Session session)
    {
        session.Continue.Set();
        try
        {
            await owner.DisposeAsync();
        }
        catch (Exception)
        {
            // 保存された停止エラーとは別に、試験のスレッド回収を確認します。
        }

        Assert.False(owner.IsAlive);
    }

    private sealed class Session : ISelectiveInputHookSession, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Continue = new(true);
        internal readonly ConcurrentQueue<Exception> Errors = new();
        internal Action<int>? OnStop;
        internal Action? OnPump;
        internal Action? OnWait;
        internal Action? OnRefresh;
        internal int StopFailures;
        internal int Stops;
        internal int Starts;
        internal bool Suspended;
        private int registered;

        public bool IsRegistered => Volatile.Read(ref registered) != 0;

        public void Start()
        {
            Starts++;
            Volatile.Write(ref registered, 1);
        }

        public bool Pump()
        {
            OnPump?.Invoke();
            return false;
        }

        public void Wait(AutoResetEvent work, int milliseconds)
        {
            OnWait?.Invoke();
            work.WaitOne(milliseconds);
        }

        public void RefreshTargets()
        {
            OnRefresh?.Invoke();
        }

        public void Stop()
        {
            Suspended = true;
            int count = Interlocked.Increment(ref Stops);
            OnStop?.Invoke(count);
            Entered.Set();
            Assert.True(Continue.Wait(5000));
            if (Interlocked.Decrement(ref StopFailures) >= 0)
            {
                var error = new InvalidOperationException($"解除失敗{count}");
                Errors.Enqueue(error);
                throw error;
            }

            Volatile.Write(ref registered, 0);
        }

        public void Dispose()
        {
            Entered.Dispose();
            Continue.Dispose();
        }
    }

    // OSの入力・画面・フックを使わず、待機期限だけを進めます。
    private sealed class Clock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        public override ITimer CreateTimer(TimerCallback callback,
            object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (gate)
            {
                timers.Add(timer);
                timer.Change(dueTime, period);
            }

            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            Timer[] ready;
            lock (gate)
            {
                Interlocked.Add(ref timestamp, elapsed.Ticks);
                ready = [.. timers.Where(timer => timer.Due is { } due && due <= timestamp)];
                foreach (Timer timer in ready)
                {
                    timer.Due = null;
                }
            }

            foreach (Timer timer in ready)
            {
                timer.Fire();
            }
        }

        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            internal long? Due;
            private bool disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (clock.gate)
                {
                    if (disposed)
                    {
                        return false;
                    }

                    Due = dueTime == Timeout.InfiniteTimeSpan
                        ? null : clock.timestamp + dueTime.Ticks;
                    return true;
                }
            }

            internal void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock.gate)
                {
                    disposed = true;
                    Due = null;
                    clock.timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
