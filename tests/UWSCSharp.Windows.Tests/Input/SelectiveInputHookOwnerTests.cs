using System.Collections.Concurrent;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputHookOwnerTests
{
    [Fact]
    public async Task StopBeforeStartupDoesNotRegisterHooks()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = new Session();
        SelectiveInputHookOwner owner = CreateOwner(() =>
        {
            entered.Set();
            Assert.True(release.Wait(5000));
            return session;
        });
        try
        {
            Assert.True(entered.Wait(5000));
            Task pending = owner.RefreshTargetsAsync();
            Task stop = owner.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.Ready);
            Assert.Equal(0, session.Starts);
            Assert.Equal(0, session.Refreshes);
            Assert.False(owner.IsAlive);
            Assert.False(session.IsRegistered);
        }
        finally
        {
            release.Set();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueuedRefreshesCompleteWithoutAdditionalNotification()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = new Session();
        SelectiveInputHookOwner owner = CreateOwner(() =>
        {
            entered.Set();
            Assert.True(release.Wait(5000));
            return session;
        });
        try
        {
            Assert.True(entered.Wait(5000));
            Task[] pending = [owner.RefreshTargetsAsync(),
                owner.RefreshTargetsAsync(), owner.RefreshTargetsAsync()];
            release.Set();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, session.Refreshes);
        }
        finally
        {
            release.Set();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreatesRunsAndStopsOnOneOwnedThread()
    {
        var session = new Session();
        int createdThread = 0;
        SelectiveInputHookOwner owner = CreateOwner(() =>
        {
            createdThread = Environment.CurrentManagedThreadId;
            return session;
        });
        await owner.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.RefreshTargetsAsync();
        await owner.DisposeAsync();
        Assert.False(owner.IsAlive);
        Assert.All(session.Threads, id => Assert.Equal(createdThread, id));
        Assert.Equal(1, session.Refreshes);
        Assert.Equal(1, session.Stops);
        Assert.False(session.IsRegistered);
    }

    [Fact]
    public async Task FailedStopKeepsOwnerAliveAndCanBeRetried()
    {
        var session = new Session { StopFailures = 2 };
        SelectiveInputHookOwner owner = CreateOwner(() => session);
        await owner.Ready;
        await Assert.ThrowsAsync<AggregateException>(() => owner.DisposeAsync().AsTask());
        Assert.True(owner.IsAlive);
        Assert.True(session.IsRegistered);
        await Assert.ThrowsAsync<AggregateException>(owner.RefreshTargetsAsync);
        await Assert.ThrowsAsync<AggregateException>(() => owner.DisposeAsync().AsTask());
        Assert.False(owner.IsAlive);
        Assert.Equal(3, session.Stops);
    }

    [Fact]
    public async Task StopErrorAfterRemovalStillEndsOwner()
    {
        var session = new Session { StopFailures = 1, RemoveBeforeError = true };
        SelectiveInputHookOwner owner = CreateOwner(() => session);
        await owner.Ready;
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.DisposeAsync().AsTask());
        Assert.False(owner.IsAlive);
        Assert.False(session.IsRegistered);
    }

    [Fact]
    public async Task PartialStartupFailureRetainsThreadUntilCleanupRetry()
    {
        var session = new Session { StartFails = true, StopFailures = 2 };
        SelectiveInputHookOwner owner = CreateOwner(() => session);
        await Assert.ThrowsAsync<ArgumentException>(() => owner.Ready);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref session.Stops) == 2, 5000));
        Assert.True(owner.IsAlive);
        await Assert.ThrowsAsync<AggregateException>(() => owner.DisposeAsync().AsTask());
        Assert.False(owner.IsAlive);
        Assert.False(session.IsRegistered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PumpOrWaitFailureRemovesHooksAndIsReported(bool wait)
    {
        var session = new Session { PumpFails = !wait, WaitFails = wait };
        SelectiveInputHookOwner owner = CreateOwner(() => session);
        await owner.Ready;
        Assert.True(SpinWait.SpinUntil(() => owner.Failure is not null, 5000));
        await Assert.ThrowsAsync<ApplicationException>(() => owner.DisposeAsync().AsTask());
        Assert.False(owner.IsAlive);
        Assert.False(session.IsRegistered);
    }

    [Fact]
    public async Task StartupFailureRejectsPendingRefreshAndReportsCleanupError()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = new Session { StartFails = true, StopFailures = 1 };
        SelectiveInputHookOwner owner = CreateOwner(() =>
        {
            entered.Set();
            Assert.True(release.Wait(5000));
            return session;
        });
        Assert.True(entered.Wait(5000));
        Task pending = owner.RefreshTargetsAsync();
        release.Set();
        await Assert.ThrowsAsync<ArgumentException>(() => owner.Ready);
        await Assert.ThrowsAsync<ArgumentException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => owner.Failure is AggregateException, 5000));
        AggregateException failure = Assert.IsType<AggregateException>(owner.Failure);
        Assert.Contains(failure.InnerExceptions, e => e is ArgumentException);
        Assert.Contains(failure.InnerExceptions, e => e is InvalidOperationException);
        await Assert.ThrowsAsync<AggregateException>(() => owner.DisposeAsync().AsTask());
        Assert.False(owner.IsAlive);
        Assert.False(session.IsRegistered);
    }

    [Fact]
    public async Task FactoryFailureDoesNotLeaveThreadRunning()
    {
        SelectiveInputHookOwner owner = CreateOwner(() => throw new ArgumentException("作成失敗"));
        await Assert.ThrowsAsync<ArgumentException>(() => owner.Ready);
        await Assert.ThrowsAsync<ArgumentException>(() => owner.DisposeAsync().AsTask());
        Assert.False(owner.IsAlive);
    }

    [Fact]
    public async Task ConcurrentDisposalSharesAttempt()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = new Session { StopEntered = entered, StopRelease = release };
        SelectiveInputHookOwner owner = CreateOwner(() => session);
        await owner.Ready;
        Task first = owner.DisposeAsync().AsTask();
        Assert.True(entered.Wait(5000));
        Task second = owner.DisposeAsync().AsTask();
        Assert.Same(first, second);
        release.Set();
        await Task.WhenAll(first, second);
        Assert.False(owner.IsAlive);
        Assert.Equal(1, session.Stops);
    }

    private static SelectiveInputHookOwner CreateOwner(
        Func<ISelectiveInputHookSession> create) => new(create, new Clock());

    // 周期更新の試験とは分け、既存の明示更新回数を固定します。
    private sealed class Clock : TimeProvider
    {
        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => 0;
    }

    private sealed class Session : ISelectiveInputHookSession
    {
        internal ConcurrentQueue<int> Threads { get; } = new();
        internal int Stops;
        internal int Starts;
        internal int Refreshes;
        internal int StopFailures;
        internal bool RemoveBeforeError;
        internal bool StartFails;
        internal bool PumpFails;
        internal bool WaitFails;
        internal ManualResetEventSlim? StopEntered;
        internal ManualResetEventSlim? StopRelease;
        private bool stopping;

        public bool IsRegistered
        {
            get; private set;
        }

        public void Start()
        {
            Starts++;
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            IsRegistered = true;
            if (StartFails)
            {
                throw new ArgumentException("開始失敗");
            }
        }

        public bool Pump()
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            if (PumpFails && !stopping)
            {
                throw new ApplicationException("通知処理失敗");
            }

            return false;
        }

        public void Wait(AutoResetEvent work, int milliseconds)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            if (WaitFails && !stopping)
            {
                throw new ApplicationException("待機失敗");
            }

            work.WaitOne(milliseconds);
        }

        public void Stop()
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            stopping = true;
            Interlocked.Increment(ref Stops);
            StopEntered?.Set();
            if (StopRelease is not null)
            {
                Assert.True(StopRelease.Wait(5000));
            }

            if (Interlocked.Decrement(ref StopFailures) >= 0)
            {
                if (RemoveBeforeError)
                {
                    IsRegistered = false;
                }

                throw new InvalidOperationException("解除失敗");
            }

            IsRegistered = false;
        }

        public void RefreshTargets()
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            Refreshes++;
        }
    }
}
