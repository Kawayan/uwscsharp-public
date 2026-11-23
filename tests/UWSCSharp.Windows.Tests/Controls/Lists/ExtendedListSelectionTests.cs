using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ExtendedListSelectionTests
{
    private static Value N(int value) => Value.Numeric(value);

    private static Value S(string text) => Value.String(text);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ListOffStillSelectsItem(bool combo, bool multiple)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "",
            combo ? 3 : multiple ? 9 : 1, 10, height: 80);
        foreach (string text in new[] { "first", "second" })
        {
            Assert.NotEqual(0, Native.SendText(list, combo ? 0x143u : 0x180u,
                0, text, 2, 1000, out _));
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("second"), N(2), N(0)], default));
        Assert.Equal(S("second"), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("missing"), N(2 | 536870912), N(0)], default));
        Assert.Equal(S("second"), await session.InvokeAsync("GETSLCTLST", [id], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiteralViewTabNameSelectsOneRowAndMissingNameClearsSelection(bool on)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "first\tsame");
        ListViewControlTests.Select(view, 3);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("first\tsame"), N(32), N(on ? 1 : 0)], default));
        Assert.Equal(3, Native.SendMessageW(view, 0x102c, 0, 3));
        Assert.Equal(0, Native.SendMessageW(view, 0x102c, 2, 3));
        Assert.Equal(0, Native.SendMessageW(view, 0x102c, 3, 3));
        Assert.Equal(S("first\tsame"), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("first\tmissing"), N(32)], default));
        Assert.Equal(S(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        ListViewControlTests.SetText(view, 3, "same\tsame");
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("same\tsame"), N(32), N(0)], default));
        Assert.Equal(3, Native.SendMessageW(view, 0x102c, 3, 3));
        Assert.Equal(0, Native.SendMessageW(view, 0x102c, 2, 3));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task SingleSelectTreatsTabAsLiteralAndClearsMissingSelection()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10, 5);
        ListViewControlTests.Select(view, 3);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("first\tsame"), N(32)], default));
        Assert.Equal(0, Native.SendMessageW(view, 0x102c, 3, 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiteralTabCheckboxChangesOnlyTheMatchingRow(bool on)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "first\tsame");
        Native.SendMessageW(view, 0x1036, 4, 4);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("first\tsame"), N(32), N(1)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("first\tsame"), N(32), N(on ? 1 : 0)], default));
        foreach (int index in new[] { 0, 2 })
        {
            Assert.Equal(on && index == 0 ? 0x2000 : 0x1000,
                Native.SendMessageW(view, 0x102c, index, 0xf000));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentVetoAndCancellationAreObserved(bool cancel)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, "first\tsame");
        using var cancellation = new CancellationTokenSource();
        int vetoes = 0;
        int changes = 0;
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice =
                Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (cancel && notice.Code == -101)
            {
                cancellation.Cancel();
            }

            if (notice.Code == -101)
            {
                changes++;
            }

            if (!cancel && notice.Code == -100)
            {
                vetoes++;
                return 1;
            }

            return 0;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", [id, S("first\tsame"), N(32)], cancellation.Token));
        }
        else
        {
            // 親の拒否は選択状態へ反映されますが、名前一致成功は維持します。
            Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
                [id, S("first\tsame"), N(32)], default));
            Assert.True(vetoes > 0);
            Assert.Equal(0, changes);
        }

        Assert.Null(fixture.CallbackFailure);
        Assert.Equal(cancel ? 3 : 0, Native.SendMessageW(view, 0x102c, 0, 3));
    }
}
