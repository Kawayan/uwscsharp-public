using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class RecordingControllerTests
{
    [Fact]
    public async Task StartupFailureRestoresEditingAndAllowsNewRecorder()
    {
        var device = new Device { StartError = new IOException("登録失敗") };
        var controller = new RecordingController(() => device, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(controller.StartAsync);
        AssertIdle(controller);
        device = new Device();
        await controller.StartAsync();
        Assert.True(controller.HasRecorder);
        RecordingResult? result = await controller.StopAsync();
        Assert.Equal("recorded", result!.Value.Source);
        AssertIdle(controller);
    }

    [Fact]
    public async Task StartupTimeoutRetainsLiveWorkerAndObservesLateFailure()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new Device
        {
            Starting = pending.Task,
            DisposeError = new TimeoutException("解放待ち")
        };
        var controller = new RecordingController(() => device, TimeSpan.FromMilliseconds(30));
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(controller.StartAsync);
        Assert.All(error.InnerExceptions, failure => Assert.IsType<TimeoutException>(failure));
        AssertRecovery(controller);
        await Assert.ThrowsAsync<InvalidOperationException>(controller.StartAsync);
        pending.SetException(new IOException("期限後の登録失敗"));
        device.Running = false;
        device.StopError = new IOException("記録失敗");
        await Assert.ThrowsAsync<IOException>(() => controller.StopAsync());
        AssertIdle(controller);
    }

    [Fact]
    public async Task StopTimeoutKeepsMonitoringAndRetryReturnsRecording()
    {
        var device = new Device { StopError = new TimeoutException("停止待ち") };
        var controller = new RecordingController(() => device, TimeSpan.FromSeconds(1));
        await controller.StartAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => controller.StopAsync());
        AssertRecovery(controller);
        device.StopError = null;
        RecordingResult? result = await controller.StopAsync();
        Assert.Equal("recorded", result!.Value.Source);
        AssertIdle(controller);
    }

    [Fact]
    public async Task StoppingDoesNotBlockCallerAndDuplicateStopIsIgnored()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new Device
        {
            StopAction = () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        }
        };
        var controller = new RecordingController(() => device, TimeSpan.FromSeconds(1));
        await controller.StartAsync();
        Task<RecordingResult?> stopping = controller.StopAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(controller.IsStopping);
            Assert.False(controller.UiState.CanEdit);
            Assert.False(controller.UiState.CanRecord);
            Assert.False(controller.UiState.Monitor);
            Assert.Null(await controller.StopAsync());
        }
        finally
        {
            release.Set();
        }
        Assert.NotNull(await stopping);
        AssertIdle(controller);
    }

    [Fact]
    public async Task ActualWorkerCleanupFailureRestoresControllerAndAllowsRestart()
    {
        var hooks = new Hooks { FailRelease = true };
        var controller = new RecordingController(
            () => new BasicRecordingDevice(new BasicRecorder(hooks)), TimeSpan.FromSeconds(2));
        await controller.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StopAsync());
        Assert.Equal(2, hooks.Releases);
        AssertIdle(controller);
        hooks.FailRelease = false;
        await controller.StartAsync();
        Assert.NotNull(await controller.StopAsync());
        Assert.Equal(4, hooks.Releases);
        AssertIdle(controller);
    }

    [Fact]
    public async Task ActualWorkerStopTimeoutRecoversAfterCleanupUnblocks()
    {
        using var release = new ManualResetEventSlim();
        var hooks = new Hooks { ReleaseGate = release };
        var recorder = new BasicRecorder(hooks);
        var controller = new RecordingController(
            () => new BasicRecordingDevice(recorder), TimeSpan.FromSeconds(2));
        await controller.StartAsync();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => controller.StopAsync());
            AssertRecovery(controller);
        }
        finally
        {
            release.Set();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (recorder.IsRunning)
        {
            await Task.Delay(10, deadline.Token);
        }
        Assert.NotNull(await controller.StopAsync());
        Assert.Equal(2, hooks.Releases);
        AssertIdle(controller);
    }

    [Fact]
    public async Task ActualWorkerStartupTimeoutCanFinishCleanupLater()
    {
        using var release = new ManualResetEventSlim();
        var hooks = new Hooks { RegisterGate = release };
        var recorder = new BasicRecorder(hooks);
        var controller = new RecordingController(
            () => new BasicRecordingDevice(recorder), TimeSpan.FromMilliseconds(30));
        try
        {
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(controller.StartAsync);
            Assert.All(error.InnerExceptions, failure => Assert.IsType<TimeoutException>(failure));
            AssertRecovery(controller);
        }
        finally
        {
            release.Set();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (recorder.IsRunning)
        {
            await Task.Delay(10, deadline.Token);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StopAsync());
        Assert.Equal(2, hooks.Releases);
        AssertIdle(controller);
    }

    [Fact]
    public async Task FactoryFailureLeavesControllerReusable()
    {
        bool fail = true;
        var controller = new RecordingController(() =>
        {
            if (fail)
            {
                throw new IOException("作成失敗");
            }

            return new Device();
        }, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(controller.StartAsync);
        AssertIdle(controller);
        fail = false;
        await controller.StartAsync();
        Assert.NotNull(await controller.StopAsync());
        AssertIdle(controller);
    }

    [Fact]
    public async Task PendingStartRejectsRestartAndStopUntilReady()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new RecordingController(
            () => new Device { Starting = ready.Task }, TimeSpan.FromSeconds(5));
        Task starting = controller.StartAsync();
        Assert.True(controller.IsStarting);
        Assert.False(controller.UiState.CanStop);
        Assert.Null(await controller.StopAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(controller.StartAsync);
        ready.SetResult();
        await starting;
        Assert.NotNull(await controller.StopAsync());
        AssertIdle(controller);
    }

    [Fact]
    public async Task StartupAndCleanupFailurePreserveBothErrorsAndAllowStop()
    {
        var controller = new RecordingController(() => new Device
        {
            StartError = new IOException("登録失敗"),
            DisposeError = new InvalidOperationException("解放失敗"),
            Running = false
        }, TimeSpan.FromSeconds(1));
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(controller.StartAsync);
        Assert.IsType<IOException>(error.InnerExceptions[0]);
        Assert.IsType<InvalidOperationException>(error.InnerExceptions[1]);
        AssertRecovery(controller);
        Assert.NotNull(await controller.StopAsync());
        AssertIdle(controller);
    }

    private static void AssertIdle(RecordingController controller)
    {
        Assert.False(controller.HasRecorder);
        Assert.False(controller.IsStarting);
        Assert.False(controller.IsStopping);
        Assert.False(controller.RecoveryPending);
        Assert.True(controller.UiState.CanEdit);
        Assert.True(controller.UiState.CanRecord);
        Assert.False(controller.UiState.Monitor);
    }

    private static void AssertRecovery(RecordingController controller)
    {
        Assert.True(controller.HasRecorder);
        Assert.True(controller.RecoveryPending);
        Assert.True(controller.UiState.Monitor);
        Assert.True(controller.UiState.CanStop);
        Assert.False(controller.UiState.CanEdit);
    }

    private sealed class Device : IRecordingDevice
    {
        internal bool Running
        {
            get; set;
        }
        internal Exception? StartError
        {
            get; init;
        }
        internal Exception? DisposeError
        {
            get; init;
        }
        internal Exception? StopError
        {
            get; set;
        }
        internal Task Starting { get; init; } = Task.CompletedTask;
        internal Action? StopAction
        {
            get; init;
        }
        public bool IsRunning => Running;
        public bool WaitingForInitialRelease => false;
        public bool Overflowed => false;

        public Task StartAsync()
        {
            Running = true;
            return StartError is null ? Starting : Task.FromException(StartError);
        }

        public string Stop()
        {
            StopAction?.Invoke();
            if (StopError is not null)
            {
                throw StopError;
            }
            Running = false;
            return "recorded";
        }

        public void Dispose()
        {
            if (DisposeError is not null)
            {
                throw DisposeError;
            }
            Running = false;
        }
    }

    private sealed class Hooks : BasicRecorder.IRecordingHooks
    {
        internal ManualResetEventSlim? RegisterGate
        {
            get; init;
        }
        internal ManualResetEventSlim? ReleaseGate
        {
            get; init;
        }

        internal bool FailRelease
        {
            get; set;
        }
        internal int Releases
        {
            get; private set;
        }
        public int LastError => 5;
        public nint Register(int type, BasicRecorder.HookCallback callback)
        {
            if (RegisterGate is not null && !RegisterGate.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("試験用登録の待機期限");
            }
            return type;
        }

        public bool Unregister(nint hook)
        {
            Releases++;
            if (ReleaseGate is not null && !ReleaseGate.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("試験用解除の待機期限");
            }
            if (FailRelease)
            {
                throw new IOException("フック解除失敗");
            }
            return true;
        }
    }
}
