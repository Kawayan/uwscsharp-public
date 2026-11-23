using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class StatusContractTests
{
    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task MissingIdPreservesStatusResultTypes()
    {
        await AssertStatusResultTypesAsync(null);
    }

    [Fact]
    public async Task LiveWindowPreservesStatusResultTypes()
    {
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { });
        await AssertStatusResultTypesAsync(fixture.Root);
    }

    private static async Task AssertStatusResultTypesAsync(nint? window)
    {
        string id = window.HasValue ? $"HNDTOID({(long)window.Value})" : "-1";
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync($"""
            id = {id}
            PRINT VARTYPE(STATUS(id, ST_ICON))
            PRINT VARTYPE(STATUS(id, ST_MAXIMIZED))
            PRINT VARTYPE(STATUS(id, ST_VISIBLE))
            PRINT VARTYPE(STATUS(id, ST_ACTIVE))
            PRINT VARTYPE(STATUS(id, ST_BUSY))
            PRINT VARTYPE(STATUS(id, ST_ISID))
            PRINT VARTYPE(STATUS(id, ST_WIN64))
            PRINT VARTYPE(STATUS(id, ST_PROCESS))
            PRINT VARTYPE(STATUS(id, ST_TITLE))
            PRINT STATUS(id, ST_ISID) + 0
            """, output.Add);
        Assert.Equal(
            ["11", "11", "11", "11", "11", "11", "11", "5", "258",
                window.HasValue ? "1" : "0"], output);
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-4)]
    public async Task AcwAndStatusUseTheSamePhysicalCoordinateSystem(int context)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, title: "STATUS-ACW-" + Guid.NewGuid());
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        nint original = Native.SetThreadDpiAwarenessContext(-4);
        Assert.NotEqual(0, original);
        try
        {
            Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect initial));
            int width = initial.Right - initial.Left + 60;
            int height = initial.Bottom - initial.Top + 60;
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(context));
            nint before = GetThreadDpiAwarenessContext();
            ValueTask<Value> move = session.InvokeAsync(BuiltinFunctionNames.Acw,
                [id, Value.Numeric(20), Value.Numeric(30),
                    Value.Numeric(width), Value.Numeric(height)], default);
            Assert.True(move.IsCompletedSuccessfully);
            await move;
            Assert.Equal(before, GetThreadDpiAwarenessContext());
            int[] expected = [20, 30, width, height];
            for (int item = 1; item <= expected.Length; item++)
            {
                ValueTask<Value> read = session.InvokeAsync(BuiltinFunctionNames.Status,
                    [id, Value.Numeric(item)], default);
                Assert.True(read.IsCompletedSuccessfully);
                Assert.Equal(Value.Numeric(expected[item - 1]), await read);
                Assert.Equal(before, GetThreadDpiAwarenessContext());
            }
        }
        finally
        {
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(original));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-4)]
    public async Task GeometryUsesPhysicalPixelsAndRestoresCallerContext(int context)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint child = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            owner => child = owner.Child("STATIC", "STATUS-child", 0, 17),
            title: "STATUS-geometry-" + Guid.NewGuid());
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        foreach (nint window in new[] { fixture.Root, child })
        {
            Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
                [Value.Numeric((long)window)], default);
            nint original = Native.SetThreadDpiAwarenessContext(-4);
            Assert.NotEqual(0, original);
            try
            {
                Assert.True(Native.GetWindowRect(window, out Native.Rect outer));
                Assert.True(Native.GetClientRect(window, out Native.Rect client));
                var point = new Native.Point(0, 0);
                Assert.True(Native.ClientToScreen(window, ref point));
                int[] expected = [outer.Left, outer.Top,
                    outer.Right - outer.Left, outer.Bottom - outer.Top,
                    point.X, point.Y, client.Right - client.Left,
                    client.Bottom - client.Top];
                Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(context));
                nint before = GetThreadDpiAwarenessContext();
                for (int item = 1; item <= expected.Length; item++)
                {
                    ValueTask<Value> pending = session.InvokeAsync(BuiltinFunctionNames.Status,
                        [id, Value.Numeric(item)], default);
                    Assert.True(pending.IsCompletedSuccessfully);
                    Value actual = await pending;
                    Assert.Equal(Value.Numeric(expected[item - 1]), actual);
                    Assert.Equal(before, GetThreadDpiAwarenessContext());
                }
            }
            finally
            {
                Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(original));
            }
        }
    }

    [Fact]
    public async Task MonitorUsesZeroBasedNativeEnumeration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, title: "STATUS-monitor-" + Guid.NewGuid());
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        var monitors = new List<nint>();
        Assert.True(Native.EnumDisplayMonitors(0, 0,
            (nint monitor, nint dc, ref Native.Rect rect, nint data) =>
            {
                monitors.Add(monitor);
                return true;
            }, 0));
        int expected = monitors.IndexOf(Native.MonitorFromWindow(fixture.Root, 2));
        Assert.True(expected >= 0);
        Assert.Equal(Value.Numeric(expected), await session.InvokeAsync(
            BuiltinFunctionNames.Status, [id, Value.Numeric(20)], default));
    }

    [Fact]
    public async Task BusyDistinguishesBlockedAndResponsiveOwnedMessageLoop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        nint child = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            child = owner.Child("BUTTON", "STATUS-busy", 0, 10);
            owner.Command = (_, _) =>
            {
                entered.Set();
                Assert.True(SpinWait.SpinUntil(() => release.IsSet,
                    TimeSpan.FromSeconds(10)));
            };
        }, title: "STATUS-busy-" + Guid.NewGuid());
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync(
            BuiltinFunctionNames.Status, [id, Value.Numeric(15)], default));
        Task blocked = Task.Run(() => Native.SendMessageTimeoutW(fixture.Root,
            0x111, 1, child, Native.SmtoAbortIfHung, 10000, out _));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
                BuiltinFunctionNames.Status, [id, Value.Numeric(15)], default));
        }
        finally
        {
            release.Set();
            await blocked;
        }

        Assert.Equal(Value.Numeric(0), await session.InvokeAsync(
            BuiltinFunctionNames.Status, [id, Value.Numeric(15)], default));
    }

    [Fact]
    public async Task VisibilityMinimizationAndDestroyedIdReflectOwnedWindowLifetime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, title: "STATUS-lifetime-" + Guid.NewGuid());
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        Native.ShowWindow(fixture.Root, 7);
        Assert.True(Native.IsIconic(fixture.Root));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
            BuiltinFunctionNames.Status, [id, Value.Numeric(10)], default));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
            BuiltinFunctionNames.Status, [id, Value.Numeric(12)], default));
        Native.ShowWindow(fixture.Root, 0);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync(
            BuiltinFunctionNames.Status, [id, Value.Numeric(12)], default));
        await fixture.DisposeAsync();
        for (int item = 1; item <= 21; item++)
        {
            Value expected = item is 9 or 14 or 17
                ? Value.String("") : Value.Numeric(item == 16 ? -1 : 0);
            Assert.Equal(expected, await session.InvokeAsync(BuiltinFunctionNames.Status,
                [id, Value.Numeric(item)], default));
        }
    }

    [Fact]
    public async Task EmptyIdFailsWithoutChangingExplicitIdLookup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, title: "STATUS-" + Guid.NewGuid());
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.Status,
                [Value.Empty, Value.Numeric(21)], default));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
            BuiltinFunctionNames.Status, [id, Value.Numeric(21)], default));
        Value foreground = await session.InvokeAsync(BuiltinFunctionNames.Status,
            [Value.Numeric(0), Value.Numeric(21)], default);
        Assert.Contains(foreground, new[] { Value.Numeric(0), Value.Numeric(1) });
    }

    [Fact]
    public async Task InvalidIdRetainsTypedFallbackForEverySupportedItem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        for (int item = 1; item <= 21; item++)
        {
            Value expected = item is 9 or 14 or 17
                ? Value.String("") : Value.Numeric(item == 16 ? -1 : 0);
            Assert.Equal(expected, await session.InvokeAsync(BuiltinFunctionNames.Status,
                [Value.Numeric(-1), Value.Numeric(item)], default));
        }
    }
}
