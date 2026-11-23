using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputHookSessionStateTests
{
    [Fact]
    public async Task CallbackFailureThenFailedStopCanPumpAndRetry()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        fixture.Invoke(() =>
        {
            var backend = new Backend();
            var context = new FailingContext();
            var session = new NativeSelectiveInputHookSession(7, SelectiveInputDevices.Keyboard,
                backend, context);
            nint data = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.Copy(new byte[32], 0, data, 32);
                session.Start();
                Assert.Equal(123, backend.Callback!(0, 0x100, data));
                Assert.Same(context.Error, Assert.Throws<InvalidOperationException>(() => session.Pump()));
                backend.FailRemoval = true;
                Assert.Throws<AggregateException>(session.Stop);
                Assert.True(session.IsRegistered);
                Assert.Equal(123, backend.Callback!(0, 0x100, 1));
                session.Pump();
                backend.FailRemoval = false;
                session.Stop();
                Assert.False(session.IsRegistered);
                Assert.Equal(2, backend.Removals);
            }
            finally
            {
                backend.FailRemoval = false;
                session.Stop();
                Marshal.FreeHGlobal(data);
            }
        });
    }

    [Fact]
    public async Task QuitMessageIsReportedBeforeExplicitCleanup()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        fixture.Invoke(() =>
        {
            var session = new NativeSelectiveInputHookSession(7, SelectiveInputDevices.Keyboard,
                new Backend(), new FailingContext());
            try
            {
                session.Start();
                Assert.True(PostThreadMessageW(Native.GetCurrentThreadId(), 0x12, 0, 0));
                Assert.Throws<OperationCanceledException>(() => session.Pump());
                Assert.True(session.IsRegistered);
            }
            finally
            {
                session.Stop();
                Assert.False(session.IsRegistered);
            }
        });
    }

    [Fact]
    public async Task RefreshIncludesPopupShownAfterStart()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { },
            dpiAwarenessContext: -4);
        fixture.Invoke(() =>
        {
            var context = new NativeSelectiveInputHookContext(NativeSelectiveInputWindowTree.Capture());
            var session = new NativeSelectiveInputHookSession(fixture.Root, SelectiveInputDevices.Mouse,
                new Backend(), context);
            nint popup = 0;
            try
            {
                session.Start();
                popup = CreateWindowExW(0, "STATIC", "開始後の所有popup", 0x80000100,
                    40, 40, 120, 80, fixture.Root, 0, 0, 0);
                Assert.NotEqual(0, popup);
                Native.ShowWindow(popup, 4);
                var point = new Native.Point(20, 20);
                Assert.True(Native.ClientToScreen(popup, ref point));
                Assert.Equal(popup, Native.WindowFromPoint(point));
                Assert.Equal(fixture.Root, context.ResolvePoint(point.X, point.Y));
                session.RefreshTargets();
                Assert.Equal(popup, context.ResolvePoint(point.X, point.Y));
            }
            finally
            {
                session.Stop();
                if (popup != 0)
                {
                    Assert.True(Native.DestroyWindow(popup));
                }
            }
        });
    }

    private sealed class Backend : ISelectiveInputHookBackend
    {
        internal SelectiveInputHookProcedure? Callback;
        internal bool FailRemoval;
        internal int Removals;

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            Callback = procedure;
            return (type, 0);
        }

        public (bool Success, int Error) Remove(nint hook)
        {
            Removals++;
            return FailRemoval ? (false, 5) : (true, 0);
        }
    }

    private sealed class FailingContext : ISelectiveInputHookContext
    {
        internal readonly InvalidOperationException Error = new("対象取得に失敗");

        public nint Foreground() => throw Error;

        public nint ResolvePoint(int x, int y) => throw Error;

        public nint Next(int code, nuint message, nint data) => 123;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
}
