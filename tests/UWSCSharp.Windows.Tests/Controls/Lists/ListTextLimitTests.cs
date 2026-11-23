using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ListTextLimitTests
{
    [Theory]
    [InlineData(false, 65000)]
    [InlineData(false, 65001)]
    [InlineData(false, 65002)]
    [InlineData(true, 65000)]
    [InlineData(true, 65001)]
    [InlineData(true, 65002)]
    public async Task EnumerationUsesItsOwnScanLimit(bool combo, int length)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : 0, 10);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, new string('長', length), 2, 1000, out _);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
        Assert.Equal(Math.Min(length, 65001), Assert.Single(session.AllItems).Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ClickMatchesTheBoundedPrefix(bool combo, bool multiple)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 3 : multiple ? 8 : 0, 10);
        string prefix = new('a', 2048);
        Native.SendText(control, combo ? 0x143u : 0x180u, 0, prefix + "b", 2, 1000, out _);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String(prefix + "b"), Value.Numeric(2 | 536870912)], default));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(prefix), Value.Numeric(2)], default));
        Assert.Equal(Value.String(prefix), await session.InvokeAsync("GETSLCTLST", [id], default));
    }
}
