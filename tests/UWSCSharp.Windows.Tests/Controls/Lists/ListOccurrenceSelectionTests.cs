using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ListOccurrenceSelectionTests
{
    [Theory]
    [InlineData(null, false, 1)]
    [InlineData(0, false, 1)]
    [InlineData(1, false, -1)]
    [InlineData(-1, false, -2)]
    [InlineData(-2, false, -1)]
    [InlineData(-3, false, 1)]
    [InlineData(-99, false, -1)]
    [InlineData(0, true, 1)]
    [InlineData(-1, true, -2)]
    [InlineData(-2, true, 1)]
    public async Task ZeroSearchesAllAndNegativeIncludesDisabledInForwardOrder(int? number,
        bool ofThis, int selectedControl)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint disabled = fixture.Child("LISTBOX", "", 0x08000001, 0);
        Add(disabled, false, "only");
        nint[] controls = [fixture.Child("LISTBOX", "", 1, 10),
            fixture.Child("COMBOBOX", "", 3, 60), fixture.Child("LISTBOX", "", 1, 110)];
        Add(controls[0], false, "other");
        Add(controls[1], true, "only");
        Add(controls[2], false, "only");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        List<Value> arguments = [id, Value.String("only"), Value.Numeric(2 | 512 | 536870912 | (ofThis ? 16384 : 0)),
            Value.Numeric(1)];
        if (number.HasValue)
        {
            arguments.Add(Value.Numeric(number.Value));
        }

        Assert.Equal(Value.Numeric(selectedControl != -1 ? 1 : 0),
            await session.InvokeAsync("CLKITEM", arguments, default));
        Assert.Equal((nint)(selectedControl == -2 ? 0 : -1),
            Native.SendMessageW(disabled, 0x188, 0, 0));
        for (int index = 0; index < controls.Length; index++)
        {
            Assert.Equal((nint)(index == selectedControl ? 0 : -1),
                Native.SendMessageW(controls[index], index == 1 ? 0x147u : 0x188u, 0, 0));
        }
    }

    [Fact]
    public async Task MinimumNumberOverflowsBeforeSelectingValidList()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("LISTBOX", "", 1, 10);
        Add(control, false, "item");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAsync<OverflowException>(async () => await session.InvokeAsync("CLKITEM",
            [id, Value.String("item"), Value.Numeric(2), Value.Numeric(1), Value.Numeric(int.MinValue)], default));
        Assert.Equal((nint)(-1), Native.SendMessageW(control, 0x188, 0, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultNumberMatchesLiteralTabOrContinuesToMultipleList(bool literal)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint single = fixture.Child("LISTBOX", "", 1, 10);
        nint multiple = fixture.Child("LISTBOX", "", 9, 60);
        Add(single, false, literal ? "first\tsecond" : "first");
        Add(multiple, false, "first", "second");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("first\tsecond"), Value.Numeric(2 | 512)], default));
        Assert.Equal((nint)(literal ? 0 : -1), Native.SendMessageW(single, 0x188, 0, 0));
        Assert.Equal((nint)(literal ? 0 : 1), Native.SendMessageW(multiple, 0x187, 0, 0));
        Assert.Equal((nint)(literal ? 0 : 1), Native.SendMessageW(multiple, 0x187, 1, 0));
    }

    [Theory]
    [InlineData(false, 2, 1, 0)]
    [InlineData(true, 1, 0, 1)]
    [InlineData(true, 2, 0, 2)]
    [InlineData(true, 3, 1, 0)]
    [InlineData(true, 4, 1, 1)]
    [InlineData(true, 5, 2, 0)]
    [InlineData(true, 6, -1, -1)]
    public async Task OfThisCountsNativeSingleMatchesAcrossListAndCombo(bool ofThis, int number,
        int selectedControl, int selectedIndex)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint[] controls = [fixture.Child("LISTBOX", "", 1, 10),
            fixture.Child("COMBOBOX", "", 3, 60), fixture.Child("LISTBOX", "", 1, 110)];
        Add(controls[0], false, "other", "dup", "dup");
        Add(controls[1], true, "dup", "dup");
        Add(controls[2], false, "dup");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        int flags = 2 | 512 | (int)ClickItemFlags.Api | (ofThis ? 16384 : 0);
        Assert.Equal(Value.Numeric(selectedControl >= 0 ? 1 : 0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("dup"), Value.Numeric(flags), Value.Numeric(1), Value.Numeric(number)], default));
        for (int index = 0; index < controls.Length; index++)
        {
            Assert.Equal((nint)(index == selectedControl ? selectedIndex : -1),
                Native.SendMessageW(controls[index], index == 1 ? 0x147u : 0x188u, 0, 0));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public async Task OfThisMultipleListIgnoresNumberAndContinuesPastNoMatch(int number)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = fixture.Child("LISTBOX", "", 9, 10);
        nint second = fixture.Child("LISTBOX", "", 9, 60);
        Add(first, false, "other");
        Add(second, false, "dup", "dup");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("du\tdu"), Value.Numeric(2 | 256 | 512 | 16384),
                Value.Numeric(1), Value.Numeric(number)], default));
        Assert.Equal((nint)0, Native.SendMessageW(first, 0x187, 0, 0));
        Assert.Equal((nint)1, Native.SendMessageW(second, 0x187, 0, 0));
        Assert.Equal((nint)1, Native.SendMessageW(second, 0x187, 1, 0));
    }

    private static void Add(nint target, bool combo, params string[] items)
    {
        foreach (string item in items)
        {
            Native.SendText(target, combo ? 0x143u : 0x180u, 0, item, 2, 1000, out _);
        }
    }
}
