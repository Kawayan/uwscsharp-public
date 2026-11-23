using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AttachedScrollBarTests
{
    private static Value N(int value) => Value.Numeric(value);

    private static ButtonControlTests.Fixture CreatePhysicalFixture(uint style)
    {
        using var dpi = new PhysicalDpiScope();
        return new ButtonControlTests.Fixture(style: style);
    }

    private static void Initialize(nint window, int bar, int position)
    {
        var info = new NativeScrollControl.ScrollInfo
        {
            Size = 28,
            Mask = 7,
            Minimum = -20,
            Maximum = 100,
            Page = 10,
            Position = position
        };
        _ = NativeScrollControl.SetScrollInfo(window, bar, ref info, true);
    }

    [Theory]
    [InlineData(0, 0x100000u, 0x114u)]
    [InlineData(1, 0x200000u, 0x115u)]
    public async Task ReadsSetsClampsAndNotifiesOwner(
        int bar, uint style, uint message)
    {
        using ButtonControlTests.Fixture fixture = CreatePhysicalFixture(style);
        Initialize(fixture.Root, bar, 15);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        int[] expected = [15, -20, 100, 10, bar];
        for (int parameter = 0; parameter < expected.Length; parameter++)
        {
            Assert.Equal(N(expected[parameter]), await session.InvokeAsync(
                "GETSLIDER", [id, N(1), N(parameter)], default));
        }

        var expectedCorners = new Native.Point[4];
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect rectangle));
            for (int parameter = 5; parameter <= 8; parameter++)
            {
                var point = new Native.Point(parameter <= 6 ? rectangle.Left : rectangle.Right,
                    parameter <= 6 ? rectangle.Top : rectangle.Bottom);
                Assert.True(Native.ScreenToClient(fixture.Root, ref point));
                expectedCorners[parameter - 5] = point;
            }
        }

        for (int parameter = 5; parameter <= 8; parameter++)
        {
            Native.Point point = expectedCorners[parameter - 5];
            Assert.Equal(N(parameter is 5 or 7 ? point.X : point.Y),
                await session.InvokeAsync("GETSLIDER", [id, N(1), N(parameter)], default));
        }

        int notifications = 0;
        fixture.Scroll = (actualMessage, wParam, lParam) =>
        {
            notifications++;
            Assert.Equal(message, actualMessage);
            Assert.Equal(0, lParam);
            Assert.Equal(4, (int)wParam & 0xffff);
            Assert.Equal(NativeScrollControl.Read(fixture.Root, default, bar).Position & 0xffff,
                (int)((long)wParam >> 16) & 0xffff);
        };
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER",
            [id, N(int.MaxValue)], default));
        Assert.Equal(N(91), await session.InvokeAsync("GETSLIDER", [id], default));
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER", [id, N(91)], default));
        Assert.Equal(1, notifications);
        Assert.Equal(N(1), await session.InvokeAsync("SETSLIDER",
            [id, N(int.MinValue)], default));
        Assert.Equal(N(-20), await session.InvokeAsync("GETSLIDER", [id], default));
        Assert.Equal(2, notifications);
        Assert.Null(fixture.CallbackFailure);
        fixture.Scroll = (_, _, _) => NativeScrollControl.Write(fixture.Root, 7, default, bar);
        Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [id, N(50)], default));
        using var cancellation = new CancellationTokenSource();
        fixture.Scroll = (_, _, _) => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("SETSLIDER", [id, N(40)], cancellation.Token));
        Assert.Equal(40, NativeScrollControl.Read(fixture.Root, default, bar).Position);
        Assert.True(Native.DestroyWindow(fixture.Root));
        Assert.Equal(N(-999999), await session.InvokeAsync("GETSLIDER", [id], default));
        Assert.Equal(N(0), await session.InvokeAsync("SETSLIDER", [id, N(5)], default));
    }

    [Fact]
    public async Task IncludesDescendantBarsAndMixedControlsInGeometryOrder()
    {
        SliderControlTests.InitializeControls();
        ButtonControlTests.Fixture fixture = null!;
        await using PumpedControlFixture pumped = await PumpedControlFixture.CreateAsync(host =>
        {
            fixture = host;
            nint owner = fixture.Child("STATIC", "", 0x300000, 10, height: 60);
            Initialize(owner, 0, 11);
            Initialize(owner, 1, 22);
            fixture.Child("msctls_trackbar32", "", 0, 90);
            nint disabled = fixture.Child("STATIC", "", 0x08100000, 1);
            Initialize(disabled, 0, 33);
            nint hidden = fixture.Child("STATIC", "", 0x100000, 2);
            Initialize(hidden, 0, 44);
            Native.ShowWindow(hidden, 0);
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        var values = new List<double>();
        for (int number = 1; number <= 2; number++)
        {
            values.Add((await session.InvokeAsync("GETSLIDER", [id, N(number)], default)).Number);
        }

        Assert.Equal([11d, 22d], [.. values.Order()]);
        Assert.Equal(N(0), await session.InvokeAsync("GETSLIDER", [id, N(3)], default));
        Assert.Equal(N(-999999), await session.InvokeAsync("GETSLIDER", [id, N(4)], default));
    }
}
