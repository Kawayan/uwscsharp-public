using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class MultipleSelectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShortFlagReachesSingleComboAndMultipleLists(int kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        bool combo = kind == 1;
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "",
            combo ? 3 : kind == 2 ? 9 : 1, 10);
        Add(control, combo, "other", "head needle tail", "last needle");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        string requested = kind == 2 ? "needle\tlast" : "needle";
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM", [id, S(requested), N(2 | 512 | 536870912)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM", [id, S(requested), N(2 | 256 | 512)], default));
        Assert.Equal(S(kind == 2 ? "head needle tail\tlast needle" : "head needle tail"),
            await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(N(kind == 2 ? 0 : 1), await session.InvokeAsync("CLKITEM",
            [id, S(""), N(2 | 256 | 512 | 536870912)], default));
        if (kind != 2)
        {
            Assert.Equal(S("other"), await session.InvokeAsync("GETSLCTLST", [id], default));
        }
    }

    [Fact]
    public async Task PartialListMatchStopsBeforeTabFallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 9, 10);
        Add(list, false, "first", "tail");
        SliderControlTests.InitializeControls(8);
        nint tab = fixture.Child("SysTabControl32", "", 0, 60);
        int index = 0;
        foreach (string name in new[] { "other", "first" })
        {
            nint text = Marshal.StringToHGlobalUni(name);
            nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeTabControl.Item>());
            try
            {
                Marshal.StructureToPtr(new NativeTabControl.Item { Mask = 1, Text = text }, pointer, false);
                Native.SendMessageW(tab, 0x133e, index++, pointer);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
                Marshal.FreeHGlobal(text);
            }
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("first\tmissing"), N(2 | 8 | 512)], default));
        Assert.Equal((nint)1, Send(list, 0x187, 0));
        Assert.Equal((nint)0, Native.SendMessageW(tab, 0x130b, 0, 0));
    }

    private static Value N(int value) => Value.Numeric(value);

    private static Value S(string text) => Value.String(text);

    private static nint Send(nint target, uint message, nint wParam = 0, nint lParam = 0)
    {
        Assert.NotEqual(0, Native.SendMessageTimeoutW(target, message,
            wParam, lParam, 2, 1000, out nuint result));
        return unchecked((nint)result);
    }

    private static void Add(nint target, bool combo, params string[] values)
    {
        foreach (string value in values)
        {
            Assert.NotEqual(0, Native.SendText(target, combo ? 0x143u : 0x180u,
                0, value, 2, 1000, out nuint result));
            Assert.True(unchecked((nint)result) >= 0);
        }
    }

    [Theory]
    [InlineData(0, 1, "")]
    [InlineData(0, 2, " 日本語 ")]
    [InlineData(2, 3, " 日本語 \t")]
    [InlineData(0, 4, " 日本語 \t\tlast")]
    public async Task EmptySelectionItemsUseAccumulatedText(int first, int last,
        string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 8, 10);
        Add(list, false, "", "", " 日本語 ", "", "last");
        for (int index = first; index <= last; index++)
        {
            Send(list, 0x185, 1, index);
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(S(expected), await session.InvokeAsync("GETSLCTLST", [id], default));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(0x800)]
    [InlineData(0x801)]
    public async Task UpdatesMatchesInOrderAndPreservesPartialStateWithoutNotification(int style)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", style, 10, 75);
        Add(list, false, "日本語", "dup", "dup", "", "third", " edge ");
        List<(nint Sender, int Id, int Code, nint First, nint Last)> notifications = [];
        fixture.Command = (sender, command) => notifications.Add((sender,
            command & 0xffff, command >> 16, Send(sender, 0x187, 0), Send(sender, 0x187, 4)));
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(S(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("third\t日本語"), N(2)], default));
        Assert.Equal(S("日本語\tthird"), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Empty(notifications);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("日本語\tthird"), N(2)], default));
        Assert.Empty(notifications);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("dup\tmissing"), N(2)], default));
        Assert.Equal(S("dup\tdup"), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, S("日本語\tmissing"), N(2)], default));
        Assert.Equal(S("日本語"), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("dup\tdup"), N(2)], default));
        Assert.Equal(1, Send(list, 0x187, 1));
        Assert.Equal(1, Send(list, 0x187, 2));
        Assert.Equal(S("dup\tdup"), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("\tthird"), N(2)], default));
        Assert.Equal(S("third"), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, S("edge"), N(2)], default));
        Assert.Equal(S(" edge "), await session.InvokeAsync("GETSLCTLST", [id], default));
        await session.InvokeAsync("GETITEM", [id, N(2)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        await session.InvokeAsync("GETSLCTLST", [id], default);
        Assert.Same(snapshot, session.AllItems);
        Assert.Empty(notifications);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task RetrievesSingleSelectionAndKeepsNumbering(bool combo, int style)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint disabled = fixture.Child("LISTBOX", "", 0x08000000, 5);
        nint first = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", style, 40);
        nint second = fixture.Child("LISTBOX", "", 8, 90);
        Add(disabled, false, "disabled");
        Add(first, combo, "A&B", "日本語");
        Add(second, false, "second");
        Send(second, 0x185, 1, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(S(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        // CBS_SIMPLEの内蔵LISTBOXもRealGetWindowClassWで候補になります。
        int secondNumber = combo && style == 1 ? 3 : 2;
        Assert.Equal(S("second"), await session.InvokeAsync("GETSLCTLST", [id, N(secondNumber)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM", [id, S("A&B"), N(2)], default));
        Assert.Equal(S("A&B"), await session.InvokeAsync("GETSLCTLST", [id], default));
        foreach (int occurrence in new[] { 0, -1, int.MinValue })
        {
            Assert.Equal(S("A&B"), await session.InvokeAsync("GETSLCTLST",
                [id, N(occurrence)], default));
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETSLCTLST", [id], cancellation.Token));
        Assert.Equal(S(""), await session.InvokeAsync("GETSLCTLST", [id, N(999)], default));
        Assert.Equal(S(""), await session.InvokeAsync("GETSLCTLST", [N(-1)], default));
        Assert.True(Native.DestroyWindow(fixture.Root));
        Assert.Equal(S(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync("GETSLCTLST", [id], default));
    }

    [Fact]
    public async Task ReadsFourSelectedControlValues()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        string[] expected = ["ＭＳ ゴシック", "標準", "12", "日本語"];
        for (int i = 0; i < expected.Length; i++)
        {
            bool combo = i < 3;
            nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "",
                combo ? 3 : 8, 10 + i * 40);
            Add(control, combo, "unused", expected[i]);
            if (combo)
            {
                Send(control, 0x14e, 1);
            }
            else
            {
                Send(control, 0x185, 1, 1);
            }
        }

        const string source = """
            PRINT GETSLCTLST(id, 1)
            PRINT GETSLCTLST(id, 2)
            PRINT GETSLCTLST(id, 3)
            PRINT GETSLCTLST(id, 4)
            """;
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"id = HNDTOID({(long)fixture.Root})\n" + source, output.Add);
        Assert.Equal(expected, output);
    }

    [Fact]
    public async Task ScriptSelectsAndReadsMultipleItems()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 0x801, 10);
        Add(list, false, "日本語", "second", "third");
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                GETITEM(id, ITM_LIST)
                PRINT CLKITEM(id, "third<#TAB>日本語", CLK_LIST)
                PRINT GETSLCTLST(id)
                PRINT ALL_ITEM_LIST[1]
                """, output.Add);
        Assert.Equal(["1", "日本語\tthird", "second"], output);
    }
}
