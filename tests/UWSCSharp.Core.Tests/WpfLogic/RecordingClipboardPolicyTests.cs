using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class RecordingClipboardPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualFinishUsesTheOptionCapturedAtSuccessfulStart(bool enabled)
    {
        var device = new Device();
        RecordingController controller = Create(device);
        await controller.StartAsync(enabled);
        RecordingResult? result = await controller.StopAsync();
        Assert.Equal(enabled, result!.Value.CopyToClipboard);
        Assert.Equal(device.Source, result.Value.Source);
        Assert.Null(await controller.StopAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonManualFinishDoesNotCopy(bool closing)
    {
        RecordingController controller = Create(new Device());
        await controller.StartAsync(true);
        Assert.False((await controller.StopAsync(closing
            ? RecordingStopReason.OwnerClosing : RecordingStopReason.WorkerStopped))!.Value.CopyToClipboard);
    }

    [Theory]
    [InlineData("", false, "")]
    [InlineData(" \t\r\n", false, "")]
    [InlineData("PRINT 1", true, "")]
    [InlineData("PRINT 1", false, "欠落の可能性")]
    public async Task IncompleteResultsDoNotCopy(string source, bool overflowed, string warning)
    {
        RecordingController controller = Create(new Device { Source = source, Overflowed = overflowed, Warning = warning });
        await controller.StartAsync(true);
        Assert.False((await controller.StopAsync())!.Value.CopyToClipboard);
    }

    [Fact]
    public async Task NewSessionDoesNotReuseThePreviousChoice()
    {
        RecordingController controller = Create(new Device());
        await controller.StartAsync(true);
        Assert.True((await controller.StopAsync())!.Value.CopyToClipboard);
        await controller.StartAsync();
        Assert.False((await controller.StopAsync())!.Value.CopyToClipboard);
        await controller.StartAsync(true);
        Assert.True((await controller.StopAsync())!.Value.CopyToClipboard);
    }

    [Fact]
    public async Task PendingStartCannotFinishOrReplaceTheCapturedChoice()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingController controller = Create(new Device { Starting = ready.Task });
        Task start = controller.StartAsync(true);
        try
        {
            Assert.Null(await controller.StopAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(false));
        }
        finally
        {
            ready.SetResult();
            await start;
        }
        Assert.True((await controller.StopAsync())!.Value.CopyToClipboard);
    }

    [Fact]
    public async Task FailedStopConsumesCopyPermissionBeforeRetry()
    {
        var device = new Device { StopError = new IOException("停止失敗") };
        RecordingController controller = Create(device);
        await controller.StartAsync(true);
        await Assert.ThrowsAsync<IOException>(() => controller.StopAsync());
        Assert.True(controller.HasRecorder);
        device.StopError = null;
        Assert.False((await controller.StopAsync())!.Value.CopyToClipboard);
        await controller.StartAsync(true);
        Assert.True((await controller.StopAsync())!.Value.CopyToClipboard);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrTimedOutStartCannotCopyOnRecovery(bool timeout)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var device = new Device
        {
            Starting = timeout ? ready.Task : Task.FromException(new IOException("開始失敗")),
            DisposeError = new IOException("回収失敗")
        };
        var controller = new RecordingController(() => device, TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAsync<AggregateException>(() => controller.StartAsync(true));
        Assert.True(controller.HasRecorder);
        ready.TrySetResult();
        Assert.False((await controller.StopAsync())!.Value.CopyToClipboard);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstAcceptedFinishReasonWins(bool manualFirst)
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
        RecordingController controller = Create(device);
        await controller.StartAsync(true);
        Task<RecordingResult?> first = controller.StopAsync(manualFirst
            ? RecordingStopReason.Manual : RecordingStopReason.OwnerClosing);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await controller.StopAsync(manualFirst
                ? RecordingStopReason.OwnerClosing : RecordingStopReason.Manual));
        }
        finally
        {
            release.Set();
        }
        Assert.Equal(manualFirst, (await first)!.Value.CopyToClipboard);
        Assert.Equal(1, device.Stops);
    }

    private static RecordingController Create(Device device)
        => new(() => device, TimeSpan.FromSeconds(2));

    private sealed class Device : IRecordingDevice
    {
        internal Task Starting { get; init; } = Task.CompletedTask;
        internal string Source { get; init; } = "PRINT \"日本語😀\"\r\n";
        internal Exception? StopError
        {
            get; set;
        }
        internal Exception? DisposeError
        {
            get; init;
        }
        internal Action? StopAction
        {
            get; init;
        }
        internal int Stops
        {
            get; private set;
        }
        public bool IsRunning
        {
            get; private set;
        }
        public bool WaitingForInitialRelease => false;
        public bool Overflowed
        {
            get; init;
        }
        public string Warning { get; init; } = "";

        public Task StartAsync()
        {
            IsRunning = true;
            return Starting;
        }

        public string Stop()
        {
            Stops++;
            StopAction?.Invoke();
            if (StopError is not null)
            {
                throw StopError;
            }
            IsRunning = false;
            return Source;
        }

        public void Dispose()
        {
            if (DisposeError is not null)
            {
                throw DisposeError;
            }
            IsRunning = false;
        }
    }
}
