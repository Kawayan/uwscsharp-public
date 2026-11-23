using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class HookRecoveryTests
{
    [Theory]
    [InlineData(1, 14)]
    [InlineData(2, 13)]
    [InlineData(3, 0)]
    public async Task MissingHookIsReplacedAndResultRetainsWarning(int missing, int onlyType)
    {
        var hooks = new Hooks();
        var monitor = new Monitor();
        using var recorder = new BasicRecorder(hooks, monitor);
        var controller = new RecordingController(
            () => new BasicRecordingDevice(recorder), TimeSpan.FromSeconds(2));
        await controller.StartAsync();
        monitor.Trigger(missing);
        await WaitUntil(() => monitor.Resets == 1);
        Assert.Equal(1, recorder.RecoveryCount);
        Assert.True(recorder.IsRunning);
        Assert.Contains("欠落", controller.Warning);
        if (onlyType != 0)
        {
            Assert.Equal([13, 14, onlyType], hooks.Registrations);
        }
        else
        {
            Assert.Equal([13, 14, 13, 14], hooks.Registrations);
        }
        RecordingResult? result = await controller.StopAsync();
        Assert.StartsWith("// 警告:", result!.Value.Source);
        Assert.Contains("欠落", result.Value.Warning);
        Assert.False(controller.HasRecorder);
        Assert.True(controller.UiState.CanEdit);
        Assert.True(monitor.Disposed);
    }

    [Fact]
    public async Task InvalidRemovedHandleAllowsReregistration()
    {
        var hooks = new Hooks { RemovedHandle = true };
        var monitor = new Monitor();
        using var recorder = new BasicRecorder(hooks, monitor);
        await recorder.StartAsync();
        monitor.Trigger(2);
        await WaitUntil(() => monitor.Resets == 1);
        Assert.Contains("警告", recorder.Stop());
        Assert.Equal([13, 14, 13], hooks.Registrations);
    }

    [Fact]
    public async Task FailedReplacementStopsAndRestoresControllerForRestart()
    {
        var hooks = new Hooks { FailReplacement = true };
        var monitor = new Monitor();
        using var recorder = new BasicRecorder(hooks, monitor);
        var controller = new RecordingController(
            () => new BasicRecordingDevice(recorder), TimeSpan.FromSeconds(2));
        await controller.StartAsync();
        monitor.Trigger(2);
        await WaitUntil(() => !recorder.IsRunning);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.StopAsync());
        Assert.IsType<Win32Exception>(error.InnerException);
        Assert.True(controller.UiState.CanEdit);
        Assert.False(controller.HasRecorder);
        Assert.Equal(2, hooks.Releases.Count);
        Assert.True(monitor.Disposed);
    }

    [Fact]
    public async Task RepeatedLossStopsAfterThreeRecoveries()
    {
        var hooks = new Hooks();
        var monitor = new Monitor();
        using var recorder = new BasicRecorder(hooks, monitor);
        await recorder.StartAsync();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            monitor.Trigger(1);
            int expected = attempt;
            await WaitUntil(() => monitor.Resets == expected);
        }
        monitor.Trigger(1);
        await WaitUntil(() => !recorder.IsRunning);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(recorder.Stop);
        Assert.Contains("繰り返", error.InnerException!.Message);
        Assert.Equal(3, recorder.RecoveryCount);
        Assert.Equal(5, hooks.Registrations.Count);
        Assert.True(monitor.Disposed);
    }

    [Fact]
    public async Task UnexpectedUnregisterFailureDoesNotInstallDuplicateHook()
    {
        var hooks = new Hooks { ReleaseError = 5 };
        var monitor = new Monitor();
        using var recorder = new BasicRecorder(hooks, monitor);
        await recorder.StartAsync();
        monitor.Trigger(2);
        await WaitUntil(() => !recorder.IsRunning);
        Assert.Throws<InvalidOperationException>(recorder.Stop);
        Assert.Equal([13, 14], hooks.Registrations);
        Assert.Equal(3, hooks.Releases.Count);
    }

    [Fact]
    public async Task NativeHookRemovedWithoutRecorderNotificationCanBeReplaced()
    {
        var hooks = new Hooks { NativeHooks = true };
        var monitor = new Monitor();
        using var recorder = new BasicRecorder(hooks, monitor);
        await recorder.StartAsync();
        Assert.True(UnhookWindowsHookEx(hooks.FirstKeyboard));
        monitor.Trigger(2);
        await WaitUntil(() => monitor.Resets == 1);
        Assert.True(recorder.IsRunning);
        Assert.Equal([13, 14, 13], hooks.Registrations);
        Assert.StartsWith("// 警告:", recorder.Stop());
        Assert.False(recorder.IsRunning);
    }

    [Fact]
    public async Task InjectedCallbacksCannotMaskMissingPhysicalNotifications()
    {
        var hooks = new Hooks();
        var monitor = new Monitor();
        using var recorder = new BasicRecorder(hooks, monitor);
        await recorder.StartAsync();
        nint data = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.Copy(new byte[32], 0, data, 32);
            Marshal.WriteInt32(data, 0, 65);
            Marshal.WriteInt32(data, 8, 0x10);
            hooks.Callbacks[13](0, 0x100, data);
            Assert.Equal(0, monitor.HookNotifications);
            Marshal.WriteInt32(data, 8, 0);
            hooks.Callbacks[13](0, 0x101, data);
            Assert.Equal(1, monitor.HookNotifications);
            Marshal.WriteInt32(data, 12, 1);
            hooks.Callbacks[14](0, 0x200, data);
            Assert.Equal(1, monitor.HookNotifications);
            Marshal.WriteInt32(data, 12, 0);
            hooks.Callbacks[14](0, 0x200, data);
            Assert.Equal(2, monitor.HookNotifications);
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
        recorder.Stop();
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    private sealed class Monitor : IRecordingHealthMonitor
    {
        private uint thread;
        private int resets;
        internal int Resets => Volatile.Read(ref resets);
        internal int HookNotifications
        {
            get; private set;
        }
        internal bool Disposed
        {
            get; private set;
        }
        public void Start() => thread = Native.GetCurrentThreadId();
        public void Hook(int kind, uint inputTime)
        {
            HookNotifications++;
        }
        public int ProcessMessage(nint window, uint message, nuint wParam, nint lParam, uint inputTime) =>
            message == 0x8005 ? (int)wParam : 0;
        public void Reset() => Interlocked.Increment(ref resets);
        public void Dispose() => Disposed = true;

        internal void Trigger(int missing) =>
            Assert.True(PostThreadMessageW(thread, 0x8005, (nuint)missing, 0));
    }

    private sealed class Hooks : BasicRecorder.IRecordingHooks
    {
        internal bool FailReplacement
        {
            get; init;
        }
        internal bool RemovedHandle
        {
            get; init;
        }
        internal int ReleaseError
        {
            get; init;
        }
        internal bool NativeHooks
        {
            get; init;
        }
        internal nint FirstKeyboard
        {
            get; private set;
        }
        internal List<int> Registrations { get; } = [];
        internal Dictionary<int, BasicRecorder.HookCallback> Callbacks { get; } = [];
        internal List<nint> Releases { get; } = [];
        private int lastError;
        public int LastError => lastError;

        public nint Register(int type, BasicRecorder.HookCallback callback)
        {
            Registrations.Add(type);
            Callbacks[type] = callback;
            if (FailReplacement && Registrations.Count > 2)
            {
                lastError = 5;
                return 0;
            }
            nint handle = NativeHooks
                ? SetWindowsHookExW(type, callback, GetModuleHandleW(null), 0)
                : Registrations.Count;
            if (type == 13 && FirstKeyboard == 0)
            {
                FirstKeyboard = handle;
            }
            lastError = Marshal.GetLastWin32Error();
            return handle;
        }

        public bool Unregister(nint hook)
        {
            Releases.Add(hook);
            if (NativeHooks)
            {
                bool success = UnhookWindowsHookEx(hook);
                lastError = Marshal.GetLastWin32Error();
                return success;
            }
            lastError = ReleaseError != 0 ? ReleaseError
                : RemovedHandle && Releases.Count == 1 ? 1404 : 0;
            return lastError == 0;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int type, BasicRecorder.HookCallback callback,
        nint module, uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? module);
}
