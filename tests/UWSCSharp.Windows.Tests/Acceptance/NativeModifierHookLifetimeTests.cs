using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeModifierHookLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastRemovalAndShutdownReleaseHookBeforeOwnerThreadExits(bool shutdown)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        fixture.Invoke(() =>
        {
            var backend = new TrackingBackend();
            var monitor = new NativeModifierHotkeys(_ => { }, backend);
            try
            {
                Assert.True(monitor.Register(1, 6));
                Assert.True(monitor.Register(2, 2));
                Assert.Equal(1, backend.Installations);
                Assert.True(monitor.IsCallbackRooted);
                Assert.True(monitor.Unregister(1));
                Assert.True(monitor.IsCallbackRooted);
                Assert.Equal(0, backend.Removals);

                nint first = backend.Handle;
                if (shutdown)
                {
                    monitor.Shutdown();
                }
                else
                {
                    Assert.True(monitor.Unregister(2));
                }

                Assert.False(monitor.IsCallbackRooted);
                Assert.False(monitor.Contains(1));
                Assert.False(monitor.Contains(2));
                Assert.Equal(1, backend.Removals);
                VerifyInvalidHook(first);

                Assert.True(monitor.Register(3, 6));
                Assert.Equal(2, backend.Installations);
                Assert.True(monitor.IsCallbackRooted);
                nint second = backend.Handle;
                monitor.Shutdown();
                Assert.False(monitor.IsCallbackRooted);
                Assert.Equal(2, backend.Removals);
                VerifyInvalidHook(second);
            }
            finally
            {
                monitor.Shutdown();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyRemovedHookIsReleasedAndCanBeRegisteredAgain(
        bool shutdown)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { });
        fixture.Invoke(() =>
        {
            var backend = new TrackingBackend();
            var monitor = new NativeModifierHotkeys(_ => { }, backend);
            try
            {
                Assert.True(monitor.Register(1, 6));
                Assert.True(UnhookWindowsHookEx(backend.Handle));
                VerifyInvalidHook(backend.Handle);

                if (shutdown)
                {
                    monitor.Shutdown();
                }
                else
                {
                    Assert.True(monitor.Unregister(1));
                }

                Assert.False(monitor.Contains(1));
                Assert.False(monitor.IsCallbackRooted);
                Assert.True(monitor.Register(1, 6));
                Assert.True(monitor.IsCallbackRooted);
                Assert.Equal(2, backend.Installations);
                Assert.True(monitor.Unregister(1));
                Assert.False(monitor.IsCallbackRooted);
            }
            finally
            {
                monitor.Shutdown();
            }
        });
    }

    private static void VerifyInvalidHook(nint handle)
    {
        // 所有スレッドが動作中のまま、解除済みハンドルがOSから拒否されることを確認します。
        bool removed = UnhookWindowsHookEx(handle);
        int error = Marshal.GetLastWin32Error();
        Assert.False(removed);
        Assert.Equal(1404, error);
    }

    private sealed class TrackingBackend : IModifierHookBackend
    {
        private readonly NativeModifierHookBackend native = new();

        internal nint Handle
        {
            get; private set;
        }

        internal int Installations
        {
            get; private set;
        }

        internal int Removals
        {
            get; private set;
        }

        public nint Install(ModifierHookProcedure procedure)
        {
            Handle = native.Install(procedure);
            if (Handle != 0)
            {
                Installations++;
            }

            return Handle;
        }

        public bool Remove(nint hook)
        {
            bool removed = native.Remove(hook);
            if (removed)
            {
                Removals++;
            }

            return removed;
        }

        public bool IsPressed(int key) => native.IsPressed(key);

        public nint Next(int code, nuint message, nint data) => native.Next(code, message, data);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);
}
