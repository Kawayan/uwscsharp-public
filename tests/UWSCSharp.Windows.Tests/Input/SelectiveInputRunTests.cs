using System.Collections.Concurrent;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputRunTests
{
    [Fact]
    public async Task ReplacesReleasesAndRegistersAgainOnOneThread()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        SelectiveInputTarget target = Global();
        await run.ChangeAsync(target, SelectiveInputDevices.Keyboard, default);
        await run.ChangeAsync(target, SelectiveInputDevices.Mouse, default);
        await run.ChangeAsync(null, SelectiveInputDevices.None, default);
        await run.ChangeAsync(target, SelectiveInputDevices.All, default);
        await run.DisposeAsync();
        Assert.Equal(["start:1", "stop:1", "start:2", "stop:2", "start:3", "stop:3"], factory.Events);
        Assert.Single(factory.Threads.Distinct());
        Assert.False(run.IsAlive);
    }

    [Fact]
    public async Task EquivalentTargetAndDevicesDoNotReregister()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        Assert.Single(factory.Sessions);
        Assert.Equal(["start:1"], factory.Events);
    }

    [Fact]
    public async Task InvalidTargetKeepsCurrentRegistration()
    {
        var factory = new Factory();
        var platform = new Platform();
        var target = SelectiveInputTarget.Capture(7, _ => 77, platform);
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        platform.Identity = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            run.ChangeAsync(target, SelectiveInputDevices.Mouse, default));
        Assert.Equal(["start:1"], factory.Events);
        Assert.True(factory.Sessions.Single().IsRegistered);
    }

    [Fact]
    public async Task InvalidModeKeepsCurrentRegistration()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            run.ChangeAsync(Global(), SelectiveInputDevices.None, default));
        Assert.Equal(["start:1"], factory.Events);
    }

    [Fact]
    public async Task FailedReleasePreventsReplacementUntilExplicitRecovery()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        Session first = factory.Sessions.Single();
        first.StopFailures = 2;
        await Assert.ThrowsAsync<AggregateException>(() =>
            run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default));
        Assert.Single(factory.Sessions);
        Assert.True(first.IsRegistered);
        Assert.True(first.Suspended);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            run.ChangeAsync(Global(), SelectiveInputDevices.All, default));
        Assert.Equal(2, first.Stops);
        await run.ChangeAsync(null, SelectiveInputDevices.None, default);
        await run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default);
        Assert.Equal(2, factory.Sessions.Count);
        Assert.Single(factory.Threads.Distinct());
    }

    [Fact]
    public async Task TransientReleaseFailureDoesNotStartReplacement()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        factory.Sessions.Single().StopFailures = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default));
        Assert.Single(factory.Sessions);
        Assert.False(factory.Sessions.Single().IsRegistered);
        await run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default);
        Assert.Equal(2, factory.Sessions.Count);
    }

    [Fact]
    public async Task StartupFailureDoesNotRestorePreviousSetting()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        factory.FailNextStart = true;
        await Assert.ThrowsAsync<ApplicationException>(() =>
            run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default));
        Assert.Equal(["start:1", "stop:1", "start:2", "stop:2"], factory.Events);
        Assert.All(factory.Sessions, session => Assert.False(session.IsRegistered));
        await run.ChangeAsync(Global(), SelectiveInputDevices.All, default);
        Assert.Equal(3, factory.Sessions.Count);
    }

    [Fact]
    public async Task QueuedCancellationDoesNotReleaseCurrentSetting()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, stop.Token));
        Assert.Equal(["start:1"], factory.Events);
    }

    [Fact]
    public async Task RootAndWorkerShareSettingButOnlyRootExitReleasesIt()
    {
        var factory = new Factory();
        await using IWindowsAutomationSession root = Create(factory);
        IWindowsAutomationSession child = root.Fork();
        await Selective(child).SetSelectiveInputAsync(new(0, 1), default);
        await child.DisposeAsync();
        Assert.Equal(["start:1"], factory.Events);
        await Selective(root).SetSelectiveInputAsync(new(0, 2), default);
        await root.DisposeAsync();
        Assert.Equal(["start:1", "stop:1", "start:2", "stop:2"], factory.Events);
        Assert.Single(factory.Threads.Distinct());
    }

    [Fact]
    public async Task RootReleasesBeforeWaitingForWorker()
    {
        var factory = new Factory();
        IWindowsAutomationSession root = Create(factory);
        IWindowsAutomationSession child = root.Fork();
        await Selective(root).SetSelectiveInputAsync(new(0, 0), default);
        Task closing = root.DisposeAsync().AsTask();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => factory.Sessions.Single().Stops == 1, 5000));
            Assert.False(closing.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                Selective(child).SetSelectiveInputAsync(new(0, 1), default));
        }
        finally
        {
            await child.DisposeAsync();
            await closing;
        }
    }

    [Fact]
    public async Task ChildCancellationDoesNotCloseSharedSetting()
    {
        var factory = new Factory();
        await using IWindowsAutomationSession root = Create(factory);
        await using IWindowsAutomationSession child = root.Fork();
        using var rootStop = new CancellationTokenSource();
        using var childStop = new CancellationTokenSource();
        root.ConfigurePause(null, rootStop.Token);
        child.ConfigurePause(null, childStop.Token);
        await Selective(child).SetSelectiveInputAsync(new(0, 1), default);
        childStop.Cancel();
        Assert.Equal(["start:1"], factory.Events);
        await Selective(root).SetSelectiveInputAsync(new(0, 2), default);
        rootStop.Cancel();
        Assert.True(SpinWait.SpinUntil(() => factory.Sessions.Last().Stops == 1, 5000));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            Selective(root).SetSelectiveInputAsync(new(0, 1), default));
    }

    [Fact]
    public async Task SeparateRunsDoNotReleaseEachOthersSetting()
    {
        var first = new Factory();
        var second = new Factory();
        await using IWindowsAutomationSession a = Create(first);
        await using IWindowsAutomationSession b = Create(second);
        await Selective(a).SetSelectiveInputAsync(new(0, 1), default);
        await Selective(b).SetSelectiveInputAsync(new(0, 2), default);
        await a.DisposeAsync();
        Assert.Equal(["start:1", "stop:1"], first.Events);
        Assert.Equal(["start:1"], second.Events);
    }

    [Fact]
    public async Task FailedRootCleanupCanBeRetriedWithoutRepeatingOtherDisposal()
    {
        var factory = new Factory();
        IWindowsAutomationSession root = Create(factory);
        await Selective(root).SetSelectiveInputAsync(new(0, 1), default);
        factory.Sessions.Single().StopFailures = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => root.DisposeAsync().AsTask());
        Assert.True(factory.Sessions.Single().IsRegistered);
        Assert.Equal(2, factory.Sessions.Single().Stops);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Selective(root).RetrySelectiveInputCleanupAsync());
        Assert.False(factory.Sessions.Single().IsRegistered);
        Assert.Equal(3, factory.Sessions.Single().Stops);
    }

    [Fact]
    public async Task RootStopSharesInProgressReplacementRemoval()
    {
        var factory = new Factory();
        var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        Session first = factory.Sessions.Single();
        first.StopFailures = 2;
        first.OnStop = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(5000));
        };
        Task change = run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default);
        Task? stop = null;
        try
        {
            Assert.True(entered.Wait(5000));
            stop = run.DisposeAsync().AsTask();
            resume.Set();
            await Assert.ThrowsAnyAsync<Exception>(() => change);
            await Assert.ThrowsAnyAsync<Exception>(() => stop);
            Assert.Equal(2, first.Stops);
            Assert.True(first.IsRegistered);
            Assert.Single(factory.Sessions);
        }
        finally
        {
            resume.Set();
            try
            {
                await change;
            }
            catch
            {
            }
            if (stop is not null)
            {
                try
                {
                    await stop;
                }
                catch
                {
                }
            }
            first.StopFailures = 0;
            try
            {
                await run.RetryCleanupAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task DeadlinePreventsReplacementEvenBeforeCallerObservesTimeout()
    {
        var factory = new Factory();
        var clock = new Clock();
        var run = new SelectiveInputRunOwner(factory.Create, clock);
        try
        {
            await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
            factory.Sessions.Single().OnStop = () => clock.Timestamp = 5000;
            await Assert.ThrowsAsync<TimeoutException>(() =>
                run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default));
            Assert.Single(factory.Sessions);
            Assert.False(factory.Sessions.Single().IsRegistered);
        }
        finally
        {
            try
            {
                await run.DisposeAsync();
            }
            catch (TimeoutException error) when (error.Message == "待機期限を過ぎた設定を登録できません。")
            {
                // 呼出し元の期限切れ後に所有者が記録した、同じ要求の遅延通知です。
                Assert.Single(factory.Sessions);
                Assert.False(factory.Sessions.Single().IsRegistered);
            }
        }
        Assert.False(run.IsAlive);
        Assert.True(run.IsCleanupComplete);
    }

    [Fact]
    public async Task DeadlineRetainsLateOwnerFailureUntilExplicitObservation()
    {
        var factory = new Factory();
        var clock = new ManualClock();
        using var resume = new ManualResetEventSlim();
        await using var run = new SelectiveInputRunOwner(factory.Create, clock);
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        factory.Sessions.Single().OnStop = () => Assert.True(resume.Wait(5000));
        try
        {
            Task change = run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default);
            Assert.True(SpinWait.SpinUntil(() => clock.HasTimer, 5000));
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => change);
            Assert.Single(factory.Sessions);
            Assert.True(factory.Sessions.Single().IsRegistered);
            resume.Set();
            Exception? late = null;
            Assert.True(SpinWait.SpinUntil(() => (late = run.TakeFailure()) is not null, 5000));
            Assert.Equal("待機期限を過ぎた設定を登録できません。", Assert.IsType<TimeoutException>(late).Message);
            Assert.Single(factory.Sessions);
            Assert.False(factory.Sessions.Single().IsRegistered);
            Assert.Null(run.TakeFailure());
            await run.DisposeAsync();
            Assert.False(run.IsAlive);
            Assert.True(run.IsCleanupComplete);
        }
        finally
        {
            resume.Set();
        }
    }

    [Fact]
    public async Task CancellationDuringRegistrationRemovesNewSetting()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        using var cancel = new CancellationTokenSource();
        factory.OnStart = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, cancel.Token));
        Assert.False(factory.Sessions.Single().IsRegistered);
        Assert.Equal(1, factory.Sessions.Single().Stops);
    }

    [Fact]
    public async Task ExplicitReleaseSupersedesInProgressRegistration()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        factory.OnStart = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(5000));
        };
        Task registering = run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        try
        {
            Assert.True(entered.Wait(5000));
            Task skipped = run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default);
            Task release = run.ChangeAsync(null, SelectiveInputDevices.None, default);
            resume.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registering);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => skipped);
            await release;
            Assert.Single(factory.Sessions);
            Assert.False(factory.Sessions.Single().IsRegistered);
        }
        finally
        {
            resume.Set();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PublicSupersessionPreservesCancellationAndCleanupFailure(
        bool cancelRequest, bool failCleanup)
    {
        var factory = new Factory();
        await using IWindowsAutomationSession root = Create(factory);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        factory.OnStart = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(5000));
        };
        Task<Value> registering = root.InvokeAsync(BuiltinFunctionNames.LockHardEx,
            [Value.Numeric(0), Value.Numeric(1)], cancellation.Token).AsTask();
        try
        {
            Assert.True(entered.Wait(5000));
            if (failCleanup)
            {
                factory.Sessions.Single().StopFailures = 1;
            }
            Task release = Selective(root).SetSelectiveInputAsync(new(null, 0), default);
            if (cancelRequest)
            {
                cancellation.Cancel();
            }
            resume.Set();
            if (cancelRequest)
            {
                OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => registering);
                Assert.Equal(cancellation.Token, error.CancellationToken);
                Assert.Null(root.GetScriptErrorMessage(BuiltinFunctionNames.LockHardEx, error));
            }
            else if (failCleanup)
            {
                AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => registering);
                Assert.Contains(error.InnerExceptions, item => item is SelectiveInputSupersededException);
                Assert.Contains(error.InnerExceptions, item => item.Message == "解除失敗");
                Assert.Null(root.GetScriptErrorMessage(BuiltinFunctionNames.LockHardEx, error));
            }
            else
            {
                InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => registering);
                Assert.IsType<SelectiveInputSupersededException>(error.InnerException);
                Assert.Equal(error.Message, root.GetScriptErrorMessage(BuiltinFunctionNames.LockHardEx, error));
            }
            await release.WaitAsync(TimeSpan.FromSeconds(5));
            factory.OnStart = null;
            await root.InvokeAsync(BuiltinFunctionNames.LockHardEx,
                [Value.Numeric(0), Value.Numeric(2)], default);
            Assert.Equal(2, factory.Sessions.Count);
        }
        finally
        {
            resume.Set();
        }
    }

    [Fact]
    public async Task RootExitDuringStartRemovesNewRegistrationOnSameThread()
    {
        var factory = new Factory();
        var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        factory.OnStart = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(5000));
        };
        Task registering = run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        try
        {
            Assert.True(entered.Wait(5000));
            Task close = run.DisposeAsync().AsTask();
            resume.Set();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => registering);
            await close;
            Assert.False(factory.Sessions.Single().IsRegistered);
            Assert.Equal(1, factory.Sessions.Single().Stops);
            Assert.False(run.IsAlive);
        }
        finally
        {
            resume.Set();
            await run.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedChangesUseActualCancellationTokenWhenOwnerStops(bool cancelRequests)
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        factory.OnStart = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
        };
        Task first = run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, cancellation.Token);
        Task? queued = null;
        Task? closing = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            queued = run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, cancellation.Token);
            Assert.False(first.IsCompleted);
            Assert.False(queued.IsCompleted);
            if (cancelRequests)
            {
                cancellation.Cancel();
            }
            closing = run.DisposeAsync().AsTask();
            resume.Set();
            foreach (Task request in new[] { first, queued })
            {
                Exception? error = await Record.ExceptionAsync(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
                if (cancelRequests)
                {
                    OperationCanceledException cancelled = Assert.IsAssignableFrom<OperationCanceledException>(error);
                    Assert.Equal(cancellation.Token, cancelled.CancellationToken);
                }
                else
                {
                    Assert.IsType<ObjectDisposedException>(error);
                }
            }
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(factory.Sessions);
            Assert.False(factory.Sessions.Single().IsRegistered);
        }
        finally
        {
            resume.Set();
            foreach (Task? operation in new[] { first, queued, closing })
            {
                if (operation is not null)
                {
                    try
                    {
                        await operation.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch
                    {
                        // 失敗した要求も観測し、所有者の後始末へ進みます。
                    }
                }
            }
        }
    }

    [Fact]
    public async Task TimeoutRetainsLateRemovalFailureForExplicitRelease()
    {
        var factory = new Factory();
        var clock = new ManualClock();
        var run = new SelectiveInputRunOwner(factory.Create, clock);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        Session first = factory.Sessions.Single();
        int pumped;
        first.StopFailures = 1;
        first.OnStop = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(5000));
        };
        Task change = run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default);
        try
        {
            Assert.True(entered.Wait(5000));
            Assert.True(SpinWait.SpinUntil(() => clock.HasTimer, 5000));
            pumped = Volatile.Read(ref first.Pumps);
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => change);
            Assert.True(first.IsRegistered);
            resume.Set();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref first.Pumps) > pumped, 5000));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                run.ChangeAsync(null, SelectiveInputDevices.None, default));
            Assert.False(first.IsRegistered);
            Assert.Equal(2, first.Stops);
            Assert.Null(run.TakeFailure());
            Assert.Single(factory.Sessions);
        }
        finally
        {
            resume.Set();
            first.StopFailures = 0;
            await run.DisposeAsync();
        }
    }

    [Fact]
    public async Task RootCloseUsesRemainingReplacementBudget()
    {
        var factory = new Factory();
        var clock = new ManualClock();
        var run = new SelectiveInputRunOwner(factory.Create, clock);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        Session first = factory.Sessions.Single();
        first.OnStop = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(5000));
        };
        Task change = run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default);
        Task? close = null;
        try
        {
            Assert.True(entered.Wait(5000));
            Assert.True(SpinWait.SpinUntil(() => clock.HasTimer, 5000));
            clock.Advance(TimeSpan.FromSeconds(4));
            close = run.DisposeAsync().AsTask();
            clock.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<TimeoutException>(() => change);
            Assert.Same(close, await Task.WhenAny(close, Task.Delay(1000)));
            await Assert.ThrowsAsync<TimeoutException>(() => close);
            Assert.True(first.IsRegistered);
            Assert.Equal(1, first.Stops);
        }
        finally
        {
            resume.Set();
            try
            {
                await change;
            }
            catch
            {
            }
            if (close is not null)
            {
                try
                {
                    await close;
                }
                catch
                {
                }
            }
            try
            {
                await run.RetryCleanupAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task PendingChangesDoNotStarveNativeNotifications()
    {
        var factory = new Factory();
        await using var run = new SelectiveInputRunOwner(factory.Create, new Clock());
        var platform = new Platform();
        var last = SelectiveInputTarget.Capture(7, _ => 77, platform);
        Task[] requests = [];
        factory.OnStart = () =>
        {
            factory.OnStart = null;
            requests =
            [
                .. Enumerable.Range(0, 100).Select(_ =>
                                run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default)),
                run.ChangeAsync(last, SelectiveInputDevices.Keyboard, default),
            ];
            platform.OnRead = () => Assert.True(Volatile.Read(ref factory.Sessions.First().Pumps) > 0);
        };
        await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
        await Task.WhenAll(requests);
    }

    [Fact]
    public void RegistrationRollbackDoesNotRetryAfterDeadline()
    {
        var factory = new Factory();
        var session = (Session)factory.Create(Global(), SelectiveInputDevices.All);
        session.Start();
        session.AutomaticAttempts = 1;
        var clock = new Clock { Timestamp = 5000 };
        SelectiveInputStopResult result = SelectiveInputHookStop.Run(session, clock, 0);
        Assert.False(result.Stopped);
        Assert.NotNull(result.Error);
        Assert.Equal(0, session.Stops);
        Assert.True(session.Suspended);
        Assert.True(session.IsRegistered);
        session.Stop();
    }

    [Fact]
    public async Task StandaloneStartupRollbackSharesOriginalBudget()
    {
        var factory = new Factory { FailNextStart = true };
        var clock = new Clock();
        factory.OnStart = () => clock.Timestamp = 5000;
        var owner = new SelectiveInputHookOwner(() =>
        {
            var session = (Session)factory.Create(Global(), SelectiveInputDevices.All);
            session.AutomaticAttempts = 1;
            return session;
        }, clock);
        try
        {
            await Assert.ThrowsAsync<ApplicationException>(() => owner.Ready);
            Session first = factory.Sessions.Single();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref first.Pumps) > 0, 5000));
            Assert.Equal(0, first.Stops);
            Assert.True(first.IsRegistered);
            Assert.True(first.Suspended);
        }
        finally
        {
            await Assert.ThrowsAnyAsync<Exception>(() => owner.DisposeAsync().AsTask());
            Assert.False(factory.Sessions.Single().IsRegistered);
            Assert.False(owner.IsAlive);
        }
    }

    [Fact]
    public async Task StandaloneAutomaticRecoverySharesBudgetAndResultWithDispose()
    {
        var factory = new Factory { FailNextStart = true };
        var clock = new ManualClock();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        Session? first = null;
        var owner = new SelectiveInputHookOwner(() =>
        {
            first = (Session)factory.Create(Global(), SelectiveInputDevices.All);
            first.AutomaticAttempts = 1;
            first.StopFailures = 1;
            first.OnStop = () =>
            {
                entered.Set();
                Assert.True(resume.Wait(5000));
            };
            return first;
        }, clock);
        Task? close = null;
        try
        {
            await Assert.ThrowsAsync<ApplicationException>(() => owner.Ready);
            Assert.True(entered.Wait(5000));
            clock.Advance(TimeSpan.FromSeconds(4));
            close = owner.DisposeAsync().AsTask();
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Same(close, await Task.WhenAny(close, Task.Delay(1000)));
            await Assert.ThrowsAsync<TimeoutException>(() => close);
            resume.Set();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref first!.Pumps) > 0, 5000));
            Assert.Equal(1, first!.Stops);
            Assert.True(first.IsRegistered);
        }
        finally
        {
            resume.Set();
            if (close is not null)
            {
                try
                {
                    await close;
                }
                catch
                {
                }
            }
            try
            {
                await owner.DisposeAsync();
            }
            catch
            {
            }
            if (first!.IsRegistered)
            {
                // 進行中操作の再待機と、完了後の明示回収を区別します。
                try
                {
                    await owner.DisposeAsync();
                }
                catch
                {
                }
            }

            Assert.False(first.IsRegistered);
            Assert.False(owner.IsAlive);
        }
    }

    [Fact]
    public async Task TimeoutDuringReplacementRegistrationRemovesNewHook()
    {
        var factory = new Factory();
        var clock = new ManualClock();
        var run = new SelectiveInputRunOwner(factory.Create, clock);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        try
        {
            await run.ChangeAsync(Global(), SelectiveInputDevices.Keyboard, default);
            factory.OnStart = () =>
            {
                entered.Set();
                Assert.True(resume.Wait(5000));
            };
            Task change = run.ChangeAsync(Global(), SelectiveInputDevices.Mouse, default);
            Assert.True(entered.Wait(5000));
            Assert.True(SpinWait.SpinUntil(() => clock.HasTimer, 5000));
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => change);
            resume.Set();
            Assert.True(SpinWait.SpinUntil(() => factory.Sessions.Count == 2
                && factory.Sessions.All(session => !session.IsRegistered), 5000));
            Assert.Equal(["start:1", "stop:1", "start:2", "stop:2"], factory.Events);
        }
        finally
        {
            resume.Set();
            try
            {
                await run.DisposeAsync();
            }
            catch
            {
                // 進行中の停止は、新しい明示待機で完了を回収します。
                try
                {
                    await run.RetryCleanupAsync();
                }
                catch
                {
                }
            }

            Assert.False(run.IsAlive);
            Assert.All(factory.Sessions, session => Assert.False(session.IsRegistered));
        }
    }

    private static IWindowsAutomationSession Create(Factory factory) =>
        new WindowsAutomation(_ => throw new InvalidOperationException("入力送信を使いません。"),
            swappedMouseButtons: false, selectiveInputFactory: factory.Create,
            selectiveInputPlatform: new Platform(), selectiveInputClock: new Clock()).CreateSession();

    private static ISelectiveInputOwnershipSession Selective(IWindowsAutomationSession session) =>
        Assert.IsAssignableFrom<ISelectiveInputOwnershipSession>(session);

    private static SelectiveInputTarget Global() =>
        SelectiveInputTarget.Capture(0, _ => throw new InvalidOperationException(), new Platform());

    private sealed class Clock : TimeProvider
    {
        public override long TimestampFrequency => 1000;

        internal long Timestamp;

        public override long GetTimestamp() => Interlocked.Read(ref Timestamp);
    }

    private sealed class Platform : IWindowIdentityPlatform
    {
        internal WindowIdentity? Identity = new(1, 2);
        internal Action? OnRead;

        public WindowIdentity? ReadIdentity(nint window)
        {
            OnRead?.Invoke();
            return Identity;
        }
    }

    private sealed class Factory
    {
        internal readonly ConcurrentQueue<string> Events = new();
        internal readonly ConcurrentQueue<int> Threads = new();
        internal readonly ConcurrentQueue<Session> Sessions = new();
        internal bool FailNextStart;
        internal Action? OnStart;

        internal ISelectiveInputHookSession Create(SelectiveInputTarget target, SelectiveInputDevices devices)
        {
            var session = new Session(this, Sessions.Count + 1, FailNextStart);
            FailNextStart = false;
            Sessions.Enqueue(session);
            return session;
        }
    }

    // 入力・画面・フックを操作せず、呼出し順と所有スレッドだけを観測します。
    private sealed class Session(Factory factory, int number, bool failStart) : ISelectiveInputHookSession
    {
        internal int Stops;
        internal int Pumps;
        internal int AutomaticAttempts = 2;
        internal int StopFailures;
        internal bool Suspended;
        internal Action? OnStop;
        private int registered;

        public bool IsRegistered => Volatile.Read(ref registered) != 0;

        public void Start()
        {
            Log("start");
            Volatile.Write(ref registered, 1);
            factory.OnStart?.Invoke();
            if (failStart)
            {
                throw new ApplicationException("開始失敗");
            }
        }

        public bool Pump()
        {
            Interlocked.Increment(ref Pumps);
            return false;
        }

        public void Wait(AutoResetEvent work, int milliseconds) => work.WaitOne(milliseconds);

        public void Stop()
        {
            Suspended = true;
            Log("stop");
            Interlocked.Increment(ref Stops);
            OnStop?.Invoke();
            if (Interlocked.Decrement(ref StopFailures) >= 0)
            {
                throw new InvalidOperationException("解除失敗");
            }

            Volatile.Write(ref registered, 0);
        }

        public int TakeStopAttempts()
        {
            int remaining = AutomaticAttempts;
            AutomaticAttempts = 2;
            return remaining;
        }

        public void Suspend() => Suspended = true;

        public void RefreshTargets()
        {
        }

        private void Log(string action)
        {
            factory.Events.Enqueue($"{action}:{number}");
            factory.Threads.Enqueue(Environment.CurrentManagedThreadId);
        }
    }
    // OSの入力・画面・フックを使わず、待機期限だけを進めます。
    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private long timestamp;

        internal bool HasTimer
        {
            get
            {
                lock (gate)
                {
                    return timers.Count != 0;
                }
            }
        }

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

        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
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
