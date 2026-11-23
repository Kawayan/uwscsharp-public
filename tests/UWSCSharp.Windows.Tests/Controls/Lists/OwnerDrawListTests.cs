using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class OwnerDrawListTests
{
    [Theory]
    [InlineData(0x10)]
    [InlineData(0x20)]
    public async Task MultipleOwnerDrawSelectionUsesStoredStrings(int drawing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", drawing | 0x48, 10, height: 100);
        Assert.NotEqual(0, Native.SendText(list, 0x180, 0, " 日本語 ", 2, 1000, out _));
        Assert.NotEqual(0, Native.SendText(list, 0x180, 0, "second", 2, 1000, out _));
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("second\t日本語"), Value.Numeric(2)], default));
        Assert.Equal(Value.String(" 日本語 \tsecond"),
            await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false, 0x10)]
    [InlineData(false, 0x20)]
    [InlineData(true, 0x10)]
    [InlineData(true, 0x20)]
    public async Task HasStringsSupportsItemReadSelectionAndSelectedText(bool combo, int drawing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "",
            drawing | (combo ? 0x203 : 0x40), 10, height: 100);
        foreach (string text in new[] { " 日本語 ", "&second...", "" })
        {
            Assert.NotEqual(0, Native.SendText(list, combo ? 0x143u : 0x180u,
                0, text, 2, 1000, out _));
        }

        Native.SendMessageW(list, combo ? 0x14eu : 0x186u, 0, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal((nint)3, Native.SendMessageW(list, combo ? 0x146u : 0x18bu, 0, 0));
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(2)], default));
        Assert.Equal(["日本語", "&second..."], session.AllItems);
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal(Value.String(" 日本語 "), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("&second..."), Value.Numeric(2)], default));
        Assert.Equal(Value.String("&second..."), await session.InvokeAsync("GETSLCTLST", [id], default));
        Native.SendMessageW(list, combo ? 0x14eu : 0x186u, 2, 0);
        Assert.Equal(Value.String(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Same(snapshot, session.AllItems);
        Assert.Null(fixture.CallbackFailure);
    }
}
