using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputEmergencyStopTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopPreparationFailureDoesNotSkipSuspensionOrHideItsError(bool failSuspension)
    {
        var hook = new PreparationFailureHook { FailSuspension = failSuspension };
        SelectiveInputStopResult result = SelectiveInputHookStop.Run(hook,
            TimeProvider.System, TimeProvider.System.GetTimestamp());
        Assert.True(result.Stopped);
        Assert.True(hook.Suspended);
        if (failSuspension)
        {
            AggregateException errors = Assert.IsType<AggregateException>(result.Error);
            Assert.Equal(new[] { hook.PreparationError, hook.SuspensionError }, errors.InnerExceptions);
        }
        else
        {
            Assert.Same(hook.PreparationError, result.Error);
        }
    }

    [Fact]
    public async Task BusyRequestsAreCoalescedAndTheLatchSurvivesDeactivation()
    {
        var emergency = new SelectiveInputEmergencyStop(new());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        emergency.Attach(() =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            return complete.Task;
        });
        emergency.SetActive(true);
        try
        {
            emergency.Request();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int count = 0; count < 100; count++)
            {
                emergency.Request();
            }

            Assert.Equal(1, Volatile.Read(ref calls));
            emergency.SetActive(false);
            Assert.Throws<InvalidOperationException>(emergency.ThrowIfRequested);
        }
        finally
        {
            complete.TrySetResult();
            await WaitAsync(() => !emergency.IsDispatching);
            await emergency.DisposeExecutionAsync();
        }
    }

    [Fact]
    public async Task CallbackRequestsDoNotWaitForCancellationCallbacksOrCleanup()
    {
        var registry = new SelectiveInputEmergencyRegistry();
        var emergency = new SelectiveInputEmergencyStop(registry);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using CancellationTokenRegistration registration = emergency.Token.Register(() =>
        {
            entered.Set();
            Assert.True(release.Wait(5000));
        });
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        emergency.Attach(() => { cleanup.TrySetResult(); return Task.CompletedTask; });
        emergency.SetActive(true);
        try
        {
            await Task.Run(registry.RequestAll).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(emergency.IsRequested);
            Assert.True(entered.Wait(3000));
            await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(emergency.Token.IsCancellationRequested);
        }
        finally
        {
            release.Set();
            await emergency.DisposeExecutionAsync();
        }
    }

    [Fact]
    public async Task AllTargetsAreRequestedButInactiveRunsAreUntouched()
    {
        var registry = new SelectiveInputEmergencyRegistry();
        var first = new SelectiveInputEmergencyStop(registry);
        var second = new SelectiveInputEmergencyStop(registry);
        var idle = new SelectiveInputEmergencyStop(registry);
        first.SetActive(true);
        second.SetRetained(true);
        idle.SetActive(true);
        idle.SetActive(false);
        registry.RequestAll();
        Assert.True(first.IsRequested);
        Assert.True(second.IsRequested);
        Assert.False(idle.IsRequested);
        await WaitAsync(() => !first.IsDispatching && !second.IsDispatching);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.False(idle.Token.IsCancellationRequested);
        await first.DisposeExecutionAsync();
        await second.DisposeExecutionAsync();
        await idle.DisposeExecutionAsync();
    }

    [Fact]
    public async Task EndedRetainedRunCanRetryWithoutCancellingItsDisposedToken()
    {
        var registry = new SelectiveInputEmergencyRegistry();
        var emergency = new SelectiveInputEmergencyStop(registry);
        int retries = 0;
        emergency.Attach(() => { Interlocked.Increment(ref retries); return Task.CompletedTask; });
        emergency.SetRetained(true);
        await emergency.DisposeExecutionAsync();
        registry.RequestAll();
        await WaitAsync(() => Volatile.Read(ref retries) == 1 && !emergency.IsDispatching);
        Assert.False(emergency.Token.IsCancellationRequested);
        registry.RequestAll();
        await WaitAsync(() => Volatile.Read(ref retries) == 2 && !emergency.IsDispatching);
        Assert.Throws<InvalidOperationException>(emergency.ThrowIfRequested);
    }

    [Fact]
    public async Task SynchronousCleanupFailureIsKeptOutsideTheCallback()
    {
        var emergency = new SelectiveInputEmergencyStop(new());
        var error = new InvalidOperationException("検証用の同期回収失敗");
        emergency.Attach(() => throw error);
        emergency.SetActive(true);
        emergency.Request();
        await WaitAsync(() => !emergency.IsDispatching);
        Assert.Same(error, emergency.TakeFailure());
        Assert.Null(emergency.TakeFailure());
        await emergency.DisposeExecutionAsync();
    }

    internal static async Task WaitAsync(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!completed())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class PreparationFailureHook : ISelectiveInputHookSession
    {
        internal readonly InvalidOperationException PreparationError = new("検証用の停止予算設定失敗");
        internal readonly InvalidOperationException SuspensionError = new("検証用の判定停止失敗");
        internal bool FailSuspension;
        internal bool Suspended;
        public bool IsRegistered { get; private set; } = true;
        public void PrepareStop(TimeProvider clock, long startedAt) => throw PreparationError;
        public void Suspend()
        {
            Suspended = true;
            if (FailSuspension)
            {
                throw SuspensionError;
            }
        }
        public void Stop() => IsRegistered = false;
        public void Start()
        {
        }
        public bool Pump() => false;
        public void Wait(AutoResetEvent work, int milliseconds)
        {
        }
        public void RefreshTargets()
        {
        }
    }
}
