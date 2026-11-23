using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeModifierHotkeysTests
{
    [Fact]
    public void HookDataUpdatesBothSidesWithoutRepeatingOrConsumingInput()
    {
        var backend = new Backend();
        List<int> notifications = [];
        var monitor = new NativeModifierHotkeys(notifications.Add, backend);
        try
        {
            Assert.True(monitor.Register(7, 2));
            Assert.True(monitor.Register(8, 1));
            backend.Emit(0x100, 0x11, 0x1D, 0);
            backend.Emit(0x100, 0x11, 0x1D, 0);
            backend.Emit(0x100, 0x11, 0x1D, 1);
            backend.Emit(0x101, 0x11, 0x1D, 0x80);
            backend.Emit(0x100, 0x11, 0x1D, 1);
            backend.Emit(0x101, 0x11, 0x1D, 0x81);
            backend.Emit(0x104, 0x12, 0x38, 1);
            backend.Emit(0x105, 0x12, 0x38, 0x81);
            Assert.Equal([7, 8], notifications);
            Assert.Equal(8, backend.NextCalls);
            monitor.ThrowIfFaulted();
        }
        finally
        {
            monitor.Shutdown();
        }
    }

    [Fact]
    public void NotificationFailureIsRetainedWhileLaterInputStillPassesThrough()
    {
        var backend = new Backend();
        int notifications = 0;
        var monitor = new NativeModifierHotkeys(_ =>
        {
            notifications++;
            throw new InvalidOperationException("notification failed");
        }, backend);
        try
        {
            Assert.True(monitor.Register(7, 2));
            backend.Emit(0x100, 0xA2, 0x1D, 0);
            backend.Emit(0x101, 0xA2, 0x1D, 0x80);
            backend.Emit(0x100, 0xA2, 0x1D, 0);
            Assert.Equal(1, notifications);
            Assert.Equal(3, backend.NextCalls);
            Assert.Equal("notification failed",
                Assert.Throws<InvalidOperationException>(monitor.ThrowIfFaulted).Message);
            Assert.True(monitor.Unregister(7));
            Assert.False(monitor.IsCallbackRooted);
        }
        finally
        {
            monitor.Shutdown();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LastTransitionDuringSnapshotOverridesEarlierSample(bool pressed)
    {
        const int control = 0xA2;
        const int rightControl = 0xA3;
        const int shift = 0xA0;
        var backend = new Backend();
        backend.Sample = key =>
        {
            if (key == rightControl)
            {
                backend.Emit(pressed ? 0x100u : 0x101u, control, 0x1D,
                    pressed ? 0 : 0x80);
            }

            return key == control && !pressed;
        };
        List<int> notifications = [];
        var monitor = new NativeModifierHotkeys(notifications.Add, backend);
        try
        {
            Assert.True(monitor.Register(1, 6));
            Assert.Empty(notifications);
            backend.Emit(0x100, shift, 0x2A, 0);
            Assert.Equal(pressed ? new[] { 1 } : [], notifications);
        }
        finally
        {
            monitor.Shutdown();
        }
    }

    [Fact]
    public void NonActionCodeAndUnknownMessageDoNotReadKeyboardData()
    {
        var backend = new Backend();
        var monitor = new NativeModifierHotkeys(_ => Assert.Fail("unexpected"), backend);
        try
        {
            Assert.True(monitor.Register(7, 2));
            Assert.Equal((nint)42, backend.Procedure!(-1, 0x100, 0));
            Assert.Equal((nint)42, backend.Procedure!(0, 0x400, 0));
            Assert.Equal(2, backend.NextCalls);
            monitor.ThrowIfFaulted();
        }
        finally
        {
            monitor.Shutdown();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSnapshotIsCleanedWithoutRegisteredId(bool failRemoval)
    {
        var backend = new Backend { FailSnapshot = true, FailRemoval = failRemoval };
        var monitor = new NativeModifierHotkeys(_ => { }, backend);
        try
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => monitor.Register(1, 2));
            Assert.Equal("snapshot", error.Message);
            Assert.False(monitor.Contains(1));
            Assert.Equal(failRemoval, monitor.IsCallbackRooted);
            Assert.Equal(failRemoval, error.Data.Contains("UWSCSharp.ModifierHookCleanupErrors"));
            Assert.Equal("snapshot", Assert.Throws<InvalidOperationException>(monitor.ThrowIfFaulted).Message);
        }
        finally
        {
            backend.FailRemoval = false;
            monitor.Shutdown();
        }

        Assert.False(monitor.IsCallbackRooted);
        Assert.Equal(failRemoval ? 2 : 1, backend.Removals);
    }

    [Fact]
    public void InstallationExceptionDoesNotRetainCallbackRoot()
    {
        var backend = new Backend { FailInstall = true };
        var monitor = new NativeModifierHotkeys(_ => { }, backend);
        try
        {
            Assert.Equal("install", Assert.Throws<InvalidOperationException>(() => monitor.Register(1, 2)).Message);
            Assert.False(monitor.IsCallbackRooted);
            Assert.Equal(0, backend.Removals);
        }
        finally
        {
            monitor.Shutdown();
        }
    }

    [Fact]
    public void FailedFinalRemovalRetainsBindingUntilShutdownSucceeds()
    {
        var backend = new Backend();
        var monitor = new NativeModifierHotkeys(_ => { }, backend);
        try
        {
            Assert.True(monitor.Register(1, 2));
            backend.FailRemoval = true;
            Assert.False(monitor.Unregister(1));
            Assert.True(monitor.Contains(1));
            Assert.True(monitor.IsCallbackRooted);
        }
        finally
        {
            backend.FailRemoval = false;
            monitor.Shutdown();
        }

        Assert.False(monitor.Contains(1));
        Assert.False(monitor.IsCallbackRooted);
    }

    private sealed class Backend : IModifierHookBackend
    {
        internal bool FailInstall;
        internal bool FailSnapshot;
        internal bool FailRemoval;
        internal int Removals;
        internal int NextCalls;
        internal ModifierHookProcedure? Procedure;
        internal Func<int, bool>? Sample;

        public nint Install(ModifierHookProcedure procedure)
        {
            if (FailInstall)
            {
                throw new InvalidOperationException("install");
            }

            Procedure = procedure;
            return 1;
        }

        public bool Remove(nint hook)
        {
            Assert.Equal((nint)1, hook);
            Removals++;
            return !FailRemoval;
        }

        public bool IsPressed(int key) =>
            FailSnapshot ? throw new InvalidOperationException("snapshot")
                : Sample?.Invoke(key) ?? false;

        public nint Next(int code, nuint message, nint data)
        {
            NextCalls++;
            return 42;
        }

        internal void Emit(nuint message, int key, int scan, int flags)
        {
            // KBDLLHOOKSTRUCTの先頭4個のDWORDとポインター領域を用意します。
            nint data = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.Copy(new byte[32], 0, data, 32);
                Marshal.WriteInt32(data, 0, key);
                Marshal.WriteInt32(data, 4, scan);
                Marshal.WriteInt32(data, 8, flags);
                Assert.Equal((nint)42, Procedure!(0, message, data));
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
    }
}
