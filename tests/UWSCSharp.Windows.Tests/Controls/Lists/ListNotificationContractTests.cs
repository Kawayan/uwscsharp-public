using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ListNotificationContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(1536)]
    public async Task SessionPassesMoveAndBackFlagsToComboPreparation(int flags)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("COMBOBOX", "", 3, 10);
        Native.SendText(control, 0x143, 0, "first", 2, 1000, out _);
        Native.SendText(control, 0x143, 0, "second", 2, 1000, out _);
        var platform = new CursorPlatform();
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("second"), Value.Numeric(flags | 2)], default));
        var expected = new List<string>();
        if ((flags & 1024) != 0)
        {
            expected.Add("10:20");
        }

        if ((flags & 512) != 0)
        {
            expected.Add("104:40");
            expected.Add((flags & 1024) != 0 ? "10:20" : "7:9");
        }

        Assert.Equal(expected, platform.Moves);
    }

    private sealed class CursorPlatform : OwnedListSelectionPlatform
    {
        internal List<string> Moves { get; } = [];
        private Native.Point position = new(7, 9);

        public override Native.Point Cursor() => position;

        public override Native.Rect Bounds(nint window) => new() { Left = 10, Top = 20, Right = 100, Bottom = 60 };

        public override int Metric(int index) => index == 76 ? 0 : 1000;

        public override void MoveCursor(Native.Point point)
        {
            position = point;
            Moves.Add($"{point.X}:{point.Y}");
        }
    }

    [Fact]
    public void SimpleComboRetainsDisplayedStateAfterHideMessage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("COMBOBOX", "", 1, 10);
        Assert.Equal((nint)1, Native.SendMessageW(control, 0x157, 0, 0));
        Native.SendMessageW(control, 0x14f, 0, 0);
        Assert.Equal((nint)1, Native.SendMessageW(control, 0x157, 0, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeParentDisruptionRetriesUntilThirdAttempt(bool combo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 1, 10);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "first", 2, 1000, out _);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "second", 2, 1000, out _);
        int notifications = 0;
        fixture.Command = (target, command) =>
        {
            if (target == control && ((command >> 16) & 0xffff) == 1)
            {
                notifications++;
                if (notifications is 2 or 4)
                {
                    Native.SendMessageW(control, combo ? 0x14eu : 0x186u, 0, 0);
                }
            }
        };
        var backend = new NativeListSelectionBackend(fixture.Root, control, combo, true,
            new OwnedListSelectionPlatform());
        Assert.True(ListSelectionSequence.Execute(1, combo, true, backend, default));
        Assert.Equal(6, notifications);
        Assert.Equal((nint)1, Native.SendMessageW(control, combo ? 0x147u : 0x188u, 0, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    public void NeighborKeysProduceIntermediateSelectionsAndNativeNotifications(int kind, int index)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        bool combo = kind == 2;
        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : kind, 10);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "first", 2, 1000, out _);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, "second", 2, 1000, out _);
        Native.SendMessageW(control, combo ? 0x14eu : 0x186u, index, 0);
        var observed = new List<nint>();
        fixture.Command = (target, command) =>
        {
            if (target == control && ((command >> 16) & 0xffff) == 1)
            {
                observed.Add(Native.SendMessageW(control, combo ? 0x147u : 0x188u, 0, 0));
            }
        };
        NativeControlKey.Press(control, index == 0 ? 40 : 38, default);
        NativeControlKey.Press(control, index == 0 ? 38 : 40, default);
        Assert.Equal((nint)index, Native.SendMessageW(control, combo ? 0x147u : 0x188u, 0, 0));
        if (kind == 0)
        {
            Assert.Empty(observed);
        }
        else
        {
            Assert.Equal([(nint)(index == 0 ? 1 : 0), (nint)index], observed);
        }
    }
}
