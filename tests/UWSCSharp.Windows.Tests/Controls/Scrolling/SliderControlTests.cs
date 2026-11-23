using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class SliderControlTests
{
    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task MissingIdDistinguishesSetFromLegacyQuery()
    {
        List<string> output = [];
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false);
        await new Interpreter(windows: automation).RunAsync("""
            PRINT VARTYPE(SETSLIDER(-1, 50))
            PRINT VARTYPE(SETSLIDER(-1, GET_SLIDER))
            PRINT SETSLIDER(-1, 50) + 0
            PRINT SETSLIDER(-1, GET_SLIDER)
            """, output.Add);
        Assert.Equal(["11", "5", "0", "0"], output);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommonControls
    {
        public uint Size;
        public uint Classes;
    }

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitCommonControlsEx(ref CommonControls controls);

    internal static void InitializeControls(uint classes = 4)
    {
        var controls = new CommonControls { Size = 8, Classes = classes };
        Assert.True(InitCommonControlsEx(ref controls));
    }

    private static Value N(int value) => Value.Numeric(value);

    private static void Send(nint target, uint message, nint value)
    {
        Assert.NotEqual(0, Native.SendMessageTimeoutW(target, message,
            1, value, 2, 1000, out _));
    }

    private static int Position(nint target)
    {
        Assert.NotEqual(0, Native.SendMessageTimeoutW(target, 0x400,
            0, 0, 2, 1000, out nuint result));
        return unchecked((int)result);
    }

    [Theory]
    [InlineData(0, 0x114)]
    [InlineData(2, 0x115)]
    public async Task SetsClampsAndNotifiesParentWithNativePosition(
        int style, uint notification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var controls = new CommonControls { Size = 8, Classes = 4 };
        Assert.True(InitCommonControlsEx(ref controls));
        ButtonControlTests.Fixture fixture = null!;
        nint target = 0;
        List<int> positions = [];
        List<int> codes = [];
        await using PumpedControlFixture pumped = await PumpedControlFixture.CreateAsync(owner =>
        {
            fixture = owner;
            fixture.Child("msctls_trackbar32", "", 0, 70);
            target = fixture.Child("msctls_trackbar32", "", style, 30);
            fixture.Child("msctls_trackbar32", "", 0x08000000, 1);
            nint hidden = fixture.Child("msctls_trackbar32", "", 0, 2);
            Native.ShowWindow(hidden, 0);
            Send(target, 0x407, -20);
            Send(target, 0x408, 70000);
            Send(target, 0x405, 0);
            fixture.Child("EDIT", "snapshot", 0, 110);
            fixture.Scroll = (message, wParam, lParam) =>
            {
                Assert.Equal(notification, message);
                Assert.Equal(target, lParam);
                int position = Position(target);
                Assert.Equal(position & 0xffff, ((long)wParam >> 16) & 0xffff);
                positions.Add(position);
                codes.Add((int)wParam & 0xffff);
            };
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM", [id, N(131072)], default));
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(0)], default));
        Assert.Empty(positions);
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(-12)], default));
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(65536)], default));
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(int.MaxValue)], default));
        Assert.Equal(N(70000), await session.InvokeAsync("GETSLIDER", [id], default));
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(70001)], default));
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(int.MinValue)], default));
        Assert.Equal([-12, 65536, 70000, -20], positions);
        Assert.All(codes, code => Assert.Equal(4, code));
        Assert.Null(fixture.CallbackFailure);
        Assert.Same(snapshot, session.AllItems);
        Assert.Equal(["snapshot"], session.AllItems);
        Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [id, N(5), N(3)], default));
        Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [N(int.MaxValue), N(5)], default));
        Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [N(-1), N(5)], default));
        Assert.Equal(-20, Position(target));
        fixture.Scroll = null;
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                result = SETSLIDER(id, 23, )
                PRINT result
                PRINT VARTYPE(result)
                PRINT GETSLIDER(id)
                PRINT VARTYPE(SETSLIDER(id, GET_SLIDER))
                PRINT SETSLIDER(id, GET_SLIDER)
                PRINT SETSLIDER(id, 9, 2)
                PRINT GETSLIDER(id, 2)
                """, output.Add);
        Assert.Equal(["1", "11", "23", "5", "23", "1", "9"], output);
        Assert.NotEqual(0, Native.SendMessageTimeoutW(
            fixture.Root, 0x10, 0, 0, 2, 1000, out _));
        Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [id, N(1)], default));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync("SETSLIDER", [id, N(1)], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentCorrectionReturnsFalseAndCancellationPreservesChangedValue(
        bool cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var controls = new CommonControls { Size = 8, Classes = 4 };
        Assert.True(InitCommonControlsEx(ref controls));
        using var fixture = new ButtonControlTests.Fixture();
        nint target = fixture.Child("msctls_trackbar32", "", 0, 10);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        using var cancellation = new CancellationTokenSource();
        int notifications = 0;
        fixture.Scroll = (_, _, _) =>
        {
            notifications++;
            Send(target, 0x405, 7);
            if (cancel)
            {
                cancellation.Cancel();
            }
        };
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("SETSLIDER", [id, N(50)], cancellation.Token));
        }
        else
        {
            Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [id, N(50)], default));
            cancellation.Cancel();
        }

        Assert.Equal(7, Position(target));
        Assert.Equal(1, notifications);
        Assert.Null(fixture.CallbackFailure);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("SETSLIDER", [id, N(80)], cancellation.Token));
        Assert.Equal(7, Position(target));
        Assert.Equal(1, notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestructionDuringNotificationReturnsFalse(bool destroyParent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var controls = new CommonControls { Size = 8, Classes = 4 };
        Assert.True(InitCommonControlsEx(ref controls));
        using var fixture = new ButtonControlTests.Fixture();
        nint target = fixture.Child("msctls_trackbar32", "", 0, 10);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        int notifications = 0;
        fixture.Scroll = (_, _, _) =>
        {
            notifications++;
            Assert.Equal(50, Position(target));
            Assert.True(Native.DestroyWindow(destroyParent ? fixture.Root : target));
        };
        Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [id, N(50)], default));
        Assert.Equal(1, notifications);
        Assert.Null(fixture.CallbackFailure);
        Assert.False(Native.IsWindow(target));
    }

    [Theory]
    [InlineData(0, 0x114)]
    [InlineData(1, 0x115)]
    public async Task ScrollBarUsesPageAdjustedMaximumAndSharesTrackbarOrder(
        int style, uint notification)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var controls = new CommonControls { Size = 8, Classes = 4 };
        Assert.True(InitCommonControlsEx(ref controls));
        Assert.Equal(28, Marshal.SizeOf<NativeScrollControl.ScrollInfo>());
        ButtonControlTests.Fixture fixture = null!;
        nint track = 0;
        nint target = 0;
        await using PumpedControlFixture pumped = await PumpedControlFixture.CreateAsync(owner =>
        {
            fixture = owner;
            track = fixture.Child("msctls_trackbar32", "", 0, 60);
            target = fixture.Child("SCROLLBAR", "", style, 30);
            fixture.Child("SCROLLBAR", "", 0x08000000, 1);
            nint hidden = fixture.Child("SCROLLBAR", "", 0, 2);
            Native.ShowWindow(hidden, 0);
            var info = new NativeScrollControl.ScrollInfo
            {
                Size = 28,
                Mask = 7,
                Minimum = -20,
                Maximum = 100,
                Page = 10,
                Position = 0
            };
            _ = NativeScrollControl.SetScrollInfo(target, 2, ref info, true);
        }, dpiAwarenessContext: -4);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        int[] expected = [0, -20, 100, 10, style, 10, 30, 160, 55];
        for (int parameter = 0; parameter < expected.Length; parameter++)
        {
            Assert.Equal(N(expected[parameter]), await session.InvokeAsync(
                "GETSLIDER", [id, N(1), N(parameter)], default));
        }

        int notifications = 0;
        fixture.Scroll = (message, wParam, lParam) =>
        {
            notifications++;
            Assert.Equal(notification, message);
            Assert.Equal(target, lParam);
            Assert.Equal(4, (int)wParam & 0xffff);
            Assert.Equal(NativeScrollControl.Read(target, default).Position & 0xffff,
                (int)((long)wParam >> 16) & 0xffff);
        };
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(100)], default));
        Assert.Equal(91, NativeScrollControl.Read(target, default).Position);
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(int.MaxValue)], default));
        Assert.Equal(1, notifications);
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(int.MinValue)], default));
        Assert.Equal(-20, NativeScrollControl.Read(target, default).Position);
        Assert.Equal(2, notifications);
        Assert.Null(fixture.CallbackFailure);
        fixture.Scroll = (message, wParam, lParam) =>
        {
            Assert.Equal(0x114u, message);
            Assert.Equal(track, lParam);
            Assert.Equal(4, (int)wParam & 0xffff);
        };
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(42), N(2)], default));
        Assert.Equal(42, Position(track));
        Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [id, N(42), N(3)], default));
        fixture.Scroll = (_, _, _) => NativeScrollControl.Write(target, 7, default);
        Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [id, N(50)], default));
        Assert.Equal(7, NativeScrollControl.Read(target, default).Position);
        using var cancellation = new CancellationTokenSource();
        fixture.Scroll = (_, _, _) => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("SETSLIDER", [id, N(40)], cancellation.Token));
        Assert.Equal(40, NativeScrollControl.Read(target, default).Position);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task ReadsTrackbarRangePageOrientationAndOrderedPosition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var controls = new CommonControls { Size = 8, Classes = 4 };
        Assert.True(InitCommonControlsEx(ref controls));
        ButtonControlTests.Fixture fixture = null!;
        nint vertical = 0;
        nint horizontal = 0;
        await using PumpedControlFixture pumped = await PumpedControlFixture.CreateAsync(owner =>
        {
            fixture = owner;
            vertical = fixture.Child("msctls_trackbar32", "", 2, 60);
            horizontal = fixture.Child("msctls_trackbar32", "", 0, 10);
            fixture.Child("msctls_trackbar32", "", 0x08000000, 1);
            nint hidden = fixture.Child("msctls_trackbar32", "", 0, 2);
            Native.ShowWindow(hidden, 0);
            Send(horizontal, 0x407, -20);
            Send(horizontal, 0x408, 70000);
            Send(horizontal, 0x415, 7);
            Send(horizontal, 0x405, -12);
            Send(vertical, 0x405, 42);
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        int[] expected = [-12, -20, 70000, 7, 0];
        for (int parameter = 0; parameter < expected.Length; parameter++)
        {
            Assert.Equal(N(expected[parameter]), await session.InvokeAsync(
                "GETSLIDER", [id, N(1), N(parameter)], default));
        }

        Assert.Equal(N(-12), await session.InvokeAsync("GETSLIDER", [id], default));
        Assert.Equal(N(42), await session.InvokeAsync("GETSLIDER", [id, N(2)], default));
        Assert.Equal(N(1), await session.InvokeAsync("GETSLIDER", [id, N(2), N(4)], default));
        Assert.Equal(N(0), await session.InvokeAsync("GETSLIDER", [id, N(3)], default));
        Assert.Equal(N(-999999), await session.InvokeAsync("GETSLIDER", [id, N(99)], default));
        Assert.Equal(N(-999999), await session.InvokeAsync("GETSLIDER", [N(int.MaxValue)], default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETSLIDER", [id], cancellation.Token));
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT GETSLIDER(id, , SLD_MIN)
                PRINT GETSLIDER(id, , SLD_MAX)
                PRINT GETSLIDER(id, , SLD_PAGE)
                PRINT GETSLIDER(id, 2, SLD_BAR)
                PRINT GETSLIDER(id, , SLD_POS)
                """, output.Add);
        Assert.Equal(["-20", "70000", "7", "1", "-12"], output);
        Assert.NotEqual(0, Native.SendMessageTimeoutW(
            fixture.Root, 0x10, 0, 0, 2, 1000, out _));
        Assert.Equal(N(-999999), await session.InvokeAsync("GETSLIDER", [id], default));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync("GETSLIDER", [id], default));
    }
}
