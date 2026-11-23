using System.ComponentModel;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class RecordingFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task BackgroundRecordingWarnsWhenKeyInputIsOmitted(bool background)
    {
        var hooks = new Hooks();
        using var recorder = new BasicRecorder(hooks)
        {
            BackgroundPlayback = background
        };
        await recorder.StartAsync();
        nint data = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.Copy(new byte[32], 0, data, 32);
            Marshal.WriteInt32(data, 65);
            hooks.Keyboard!(0, 0x100, data);
            hooks.Keyboard!(0, 0x101, data);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }

        string source = recorder.Stop();
        if (background)
        {
            Assert.Equal("背景再生に変換できない操作を省略しました。", recorder.RecordingWarning);
            Assert.Equal("// 警告: " + recorder.RecordingWarning + Environment.NewLine, source);
        }
        else
        {
            Assert.Empty(recorder.RecordingWarning);
            Assert.Contains("KBD(", source);
        }

        Assert.Equal(source, recorder.Stop());
    }

    [Theory]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AlreadyRemovedHooksReturnRecordingWithWarning(int removed)
    {
        var hooks = new Hooks { RemovedHooks = removed };
        using var recorder = new BasicRecorder(hooks);
        await recorder.StartAsync();
        string source = recorder.Stop();
        Assert.False(recorder.IsRunning);
        Assert.Equal(0, recorder.RecoveryCount);
        Assert.Contains($"解除済みのフックを{removed}件", recorder.RecordingWarning);
        Assert.StartsWith("// 警告: " + recorder.RecordingWarning, source);
        Assert.Equal(source, recorder.Stop());
        Assert.Equal([(nint)13, (nint)14], hooks.Released);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task RemovedHookWarningDoesNotHideOtherReleaseFailure()
    {
        var hooks = new Hooks { RemovedHooks = 1, FailRelease = true };
        using var recorder = new BasicRecorder(hooks);
        await recorder.StartAsync();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(recorder.Stop);
        Win32Exception native = Assert.IsType<Win32Exception>(error.InnerException);
        Assert.Equal(5, native.NativeErrorCode);
        Assert.Contains("解除済み", recorder.RecordingWarning);
        Assert.Equal([(nint)13, (nint)14], hooks.Released);
    }

    [Fact]
    public async Task StopDuringBlockedStartupCanBeRetriedAfterTimeout()
    {
        using var hooks = new BlockingHooks();
        using var recorder = new BasicRecorder(hooks);
        Task starting = recorder.StartAsync();
        await hooks.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Throws<TimeoutException>(() => recorder.Stop());
            Assert.True(recorder.IsRunning);
            Assert.False(starting.IsCompleted);
        }
        finally
        {
            hooks.Continue.Set();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => starting);
        recorder.Dispose();
        Assert.False(recorder.IsRunning);
        Assert.Equal([(nint)13, (nint)14], hooks.Released);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => recorder.Stop());
        Assert.IsType<ObjectDisposedException>(error.InnerException);
    }

    [Fact]
    public async Task ConcurrentStopsReleaseHooksOnceAndReturnSameResult()
    {
        var hooks = new Hooks();
        using var recorder = new BasicRecorder(hooks);
        await recorder.StartAsync();
        Task<string>[] requests = [.. Enumerable.Range(0, 8)
            .Select(_ => Task.Run(recorder.Stop))];
        string[] results = await Task.WhenAll(requests);
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal([(nint)13, (nint)14], hooks.Released);
        Assert.False(recorder.IsRunning);
    }

    [Fact]
    public async Task UnexpectedWorkerExitIsReportedInsteadOfSuccessfulRecording()
    {
        using var recorder = new BasicRecorder(new Hooks { QuitOnRegister = true });
        await recorder.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (recorder.IsRunning)
        {
            await Task.Delay(10, deadline.Token);
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => recorder.Stop());
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("予期せず", error.InnerException!.Message);
    }

    [Fact]
    public async Task FailedSecondHookReleasesFirstAndEndsWorker()
    {
        var hooks = new Hooks { FailMouse = true };
        using var recorder = new BasicRecorder(hooks);
        await Assert.ThrowsAsync<Win32Exception>(recorder.StartAsync);
        recorder.Dispose();
        Assert.False(recorder.IsRunning);
        Assert.Equal([(nint)13], hooks.Released);
    }

    [Fact]
    public async Task CleanupFailureIsReportedAfterWorkerEnds()
    {
        var hooks = new Hooks { FailRelease = true };
        using var recorder = new BasicRecorder(hooks);
        await recorder.StartAsync();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => recorder.Stop());
        AggregateException failures = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(2, failures.InnerExceptions.Count);
        Assert.All(failures.InnerExceptions, failure => Assert.IsType<Win32Exception>(failure));
        Assert.False(recorder.IsRunning);
        Assert.Equal(2, hooks.Released.Count);
        recorder.Dispose();
    }

    [Fact]
    public async Task DuplicateStartAndStartAfterDisposeAreRejected()
    {
        using var recorder = new BasicRecorder(new Hooks());
        await recorder.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(recorder.StartAsync);
        recorder.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(recorder.StartAsync);
        Assert.False(recorder.IsRunning);
    }

    [Fact]
    public async Task ThrowingCleanupStillReleasesBothHooksAndReportsBothFailures()
    {
        var hooks = new Hooks { ThrowRelease = true };
        using var recorder = new BasicRecorder(hooks);
        await recorder.StartAsync();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => recorder.Stop());
        AggregateException failures = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(2, failures.InnerExceptions.Count);
        Assert.Equal([(nint)13, (nint)14], hooks.Released);
        Assert.False(recorder.IsRunning);
    }

    [Fact]
    public async Task StartupAndCleanupFailuresAreBothReported()
    {
        var hooks = new Hooks { FailMouse = true, ThrowRelease = true };
        using var recorder = new BasicRecorder(hooks);
        AggregateException failures = await Assert.ThrowsAsync<AggregateException>(recorder.StartAsync);
        recorder.Dispose();
        Assert.IsType<Win32Exception>(failures.InnerExceptions[0]);
        Assert.IsType<IOException>(failures.InnerExceptions[1]);
        Assert.Equal([(nint)13], hooks.Released);
        Assert.False(recorder.IsRunning);
    }

    private sealed class Hooks : BasicRecorder.IRecordingHooks
    {
        // 実フックを登録せず、ホストの押下状態も読みません。
        public IReadOnlyList<int> PressedKeys() => [];

        public IReadOnlyList<int> PressedButtons() => [];

        internal int RemovedHooks
        {
            get; init;
        }

        internal bool FailMouse
        {
            get; init;
        }

        internal bool FailRelease
        {
            get; init;
        }

        internal bool ThrowRelease
        {
            get; init;
        }

        internal BasicRecorder.HookCallback? Keyboard
        {
            get; private set;
        }

        internal List<nint> Released { get; } = [];

        internal bool QuitOnRegister
        {
            get; init;
        }

        public int LastError { get; private set; } = 5;

        public nint Register(int type, BasicRecorder.HookCallback callback)
        {
            if (type == 13)
            {
                Keyboard = callback;
            }

            if (QuitOnRegister && type == 14)
            {
                PostThreadMessageW(Native.GetCurrentThreadId(), 0x12, 0, 0);
            }

            return FailMouse && type == 14 ? 0 : type;
        }

        public bool Unregister(nint hook)
        {
            Released.Add(hook);
            if ((hook == 13 && RemovedHooks >= 1)
                || (hook == 14 && RemovedHooks >= 2))
            {
                LastError = 1404;
                return false;
            }

            LastError = 5;
            if (ThrowRelease)
            {
                throw new IOException("試験用フックの解除例外");
            }
            return !FailRelease;
        }
    }

    private sealed class BlockingHooks : BasicRecorder.IRecordingHooks, IDisposable
    {
        internal TaskCompletionSource Entered
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim Continue { get; } = new();

        internal List<nint> Released { get; } = [];

        public int LastError => 5;

        public nint Register(int type, BasicRecorder.HookCallback callback)
        {
            if (type == 13)
            {
                Entered.TrySetResult();
                if (!Continue.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("試験用フック登録の待機期限を超えました。");
                }
            }

            return type;
        }

        public bool Unregister(nint hook)
        {
            Released.Add(hook);
            return true;
        }

        /// <summary>
        /// 試験用の待機ハンドルを解放します。
        /// </summary>
        public void Dispose() => Continue.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint thread,
        uint message, nuint wParam, nint lParam);
}
