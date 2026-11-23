using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTabMatchingTests
{
    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint window, int index, int value);

    private static void SetId(nint target, int id)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtrW(target, -12, id);
        }
        else
        {
            _ = SetWindowLongW(target, -12, id);
        }

        Assert.Equal(id, Native.GetDlgCtrlID(target));
    }

    [Theory]
    [InlineData("&Run...", "run", false, true)]
    [InlineData("ＡＢＣ", "ABC", false, true)]
    [InlineData("ＡＢＣsuffix", "ABC", true, true)]
    [InlineData("prefix Run suffix", "run", true, true)]
    [InlineData("prefix Run suffix", "run", false, false)]
    [InlineData("ä", "Ä", false, false)]
    [InlineData("Run\tShortcut", "run", false, true)]
    [InlineData("Run", "", true, true)]
    public async Task MatchesWithAsciiHalfWidthAndShort(string text,
        string name, bool partial, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "other", text);
        fixture.Notify = (_, _) => 1;
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(expected ? 1 : 0),
            await session.InvokeAsync("CLKITEM", [id, Value.String(name),
                Value.Numeric(8 | 512 | (partial ? 256 : 0)
                    | (expected ? 0 : 536870912))], default));
        Assert.Equal(expected && name.Length != 0 ? 1 : 0,
            Native.SendMessageW(tab, 0x130b, 0, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(null, false, false, 0)]
    [InlineData(0, false, false, 0)]
    [InlineData(1, false, false, 0)]
    [InlineData(2, false, false, 1)]
    [InlineData(-1, false, true, 0)]
    [InlineData(-2, false, true, 1)]
    [InlineData(-3, false, false, 0)]
    [InlineData(-4, false, false, 1)]
    [InlineData(-2, true, true, 1)]
    public async Task CountsAcrossControlsAndIncludesDisabledForNegative(
        int? number, bool hidden, bool disabledSelected, int index)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint disabled = TabControlTests.Create(fixture, 10,
            0x08000000, "target", "target");
        nint enabled = TabControlTests.Create(fixture, 60, 0,
            "target", "target");
        if (hidden)
        {
            Native.ShowWindow(fixture.Root, 0);
        }

        nint selected = disabledSelected ? disabled : enabled;
        List<nint> notices = [];
        fixture.Notify = (_, pointer) =>
        {
            notices.Add(Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Window);
            return 1;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        List<Value> args = [id, Value.String("target"), Value.Numeric(8 | 512), Value.Numeric(1)];
        if (number.HasValue)
        {
            args.Add(Value.Numeric(number.Value));
        }

        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM", args, default));
        Assert.Equal(index == 0 ? 2 : 3, notices.Count);
        Assert.All(notices, target => Assert.Equal(selected, target));
        Assert.Equal(index, Native.SendMessageW(selected, 0x130b, 0, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task MinimumIntegerFailsBeforeReadingTab()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "target");
        int reads = 0;
        using var hook = new ToolbarClickTextTests.Interceptor(tab, (message, _, _) =>
        {
            reads += message == 0x1304 ? 1 : 0;
            return null;
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAsync<OverflowException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String("target"),
                Value.Numeric(8), Value.Numeric(1), Value.Numeric(int.MinValue)], default));
        Assert.Equal(0, reads);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void NonnegativeIdKeepsFullWidthInBothNotifications(int id)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "target");
        SetId(tab, id);
        List<nuint> ids = [];
        fixture.Notify = (parameter, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            Assert.Equal((nint)id, parameter);
            ids.Add(notice.Id);
            return 1;
        };
        Assert.True(NativeTabControl.Select(tab, 0, default, fixture.Root,
            new OwnedListSelectionPlatform(), back: true));
        Assert.Equal(new[] { (nuint)id, (nuint)id }, ids);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(int.MinValue, false)]
    [InlineData(-1, true)]
    [InlineData(int.MinValue, true)]
    public void NegativeIdStopsBeforeItsNotification(int id, bool changed)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "first", "target");
        if (!changed)
        {
            SetId(tab, id);
        }

        List<int> codes = [];
        fixture.Notify = (_, pointer) =>
        {
            codes.Add(Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code);
            SetId(tab, id);
            return 1;
        };
        Assert.Throws<InvalidOperationException>(() => NativeTabControl.Select(
            tab, 1, default, fixture.Root, new OwnedListSelectionPlatform(), back: true));
        Assert.Equal(changed ? new[] { -552, -552 } : [], codes);
        Assert.Equal(changed ? 1 : 0, Native.SendMessageW(tab, 0x130b, 0, 0));
        Assert.Null(fixture.CallbackFailure);
    }
}
