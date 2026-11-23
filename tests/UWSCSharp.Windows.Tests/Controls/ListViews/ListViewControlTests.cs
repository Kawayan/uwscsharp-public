using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ListViewControlTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Item
    {
        public uint Mask;
        public int Index;
        public int SubItem;
        public uint State;
        public uint StateMask;
        public nint Text;
        public int Capacity;
        public int Image;
        public nint Parameter;
        public int Indent;
        public int GroupId;
        public uint Columns;
        public nint ColumnPointer;
        public nint ColumnFormats;
        public int Group;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Column
    {
        public uint Mask;
        public int Format;
        public int Width;
        public nint Text;
        public int Capacity;
        public int SubItem;
        public int Image;
        public int Order;
        public int Minimum;
        public int Default;
        public int Ideal;
    }

    private static nint Send<T>(nint view, uint message, nint index,
        T value) where T : struct
    {
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            return Native.SendMessageW(view, message, index, pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    internal static void SetText(nint view, int index, string value, int column = 0)
    {
        nint text = Marshal.StringToHGlobalUni(value);
        try
        {
            Assert.NotEqual(0, Send(view, 0x1074, index,
                new Item { Text = text, SubItem = column }));
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    internal static nint Create(ButtonControlTests.Fixture fixture,
        int top, int style = 1, string className = "SysListView32")
    {
        SliderControlTests.InitializeControls(1);
        nint view = fixture.Child(className, "", style, top);
        for (int index = 0; index < 2; index++)
        {
            nint text = Marshal.StringToHGlobalUni(index == 0 ? " Name " : "値");
            try
            {
                var column = new Column { Mask = 6, Width = 70, Text = text };
                Assert.Equal(index, Send(view, 0x1061, index, column));
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        string[] names = [" &first... ", "", "same", "same", new('長', 300)];
        for (int index = 0; index < names.Length; index++)
        {
            for (int column = 0; column < 2; column++)
            {
                nint text = Marshal.StringToHGlobalUni(column == 0 ? names[index] : $" 値{index} ");
                try
                {
                    var item = new Item { Mask = 1, Index = index, SubItem = column, Text = text };
                    nint result = Send(view, column == 0 ? 0x104du : 0x1074u,
                        column == 0 ? 0 : index, item);
                    Assert.Equal(column == 0 ? index : 1, result);
                }
                finally
                {
                    Marshal.FreeHGlobal(text);
                }
            }
        }

        return view;
    }

    private static Value N(long value) => Value.Numeric(value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerDrawStoredTextSupportsReadClickAndSelection(bool single)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = Create(fixture, 10, 0x401 | (single ? 4 : 0));
        Select(view, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(5), await session.InvokeAsync("GETITEM",
            [id, N(32), N(1), N(2)], default));
        Assert.Equal(["値0", "値1", "値2", "値3", "値4"], session.AllItems);
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal("&first...",
            (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
        Assert.Equal("値0",
            (await session.InvokeAsync("GETSLCTLST", [id, N(1), N(2)], default)).Display());
        Assert.Same(snapshot, session.AllItems);
        if (!single)
        {
            Select(view, 1);
            Assert.Equal("&first...\t",
                (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
        }

        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same"), N(32)], default));
        Assert.Equal("same", (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM",
            [id, N(32), N(1), N(-1)], default));
        Assert.Equal(["Name", "値"], session.AllItems);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(0x1001)]
    [InlineData(0x1401)]
    public async Task OwnerDataStillRejectsTextOperationsWithoutChangingSelection(int style)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(1);
        using var fixture = new ButtonControlTests.Fixture();
        nint view = fixture.Child("SysListView32", "", style, 10);
        Native.SendMessageW(view, 0x102f, 3, 0);
        Select(view, 1);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(32)], default));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await session.InvokeAsync("GETSLCTLST", [id], default));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String("missing"), N(32)], default));
        Assert.NotSame(snapshot, session.AllItems);
        Assert.Empty(session.AllItems);
        Assert.Equal((nint)3, Native.SendMessageW(view, 0x1004, 0, 0));
        Assert.Equal((nint)2, Native.SendMessageW(view, 0x102c, 1, 2));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(int.MinValue + 1)]
    [InlineData(0)]
    public async Task NonpositiveSelectedColumnUsesFirstColumn(int column)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = Create(fixture, 10);
        Select(view, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal("&first...",
            (await session.InvokeAsync("GETSLCTLST", [id, N(1), N(column)], default)).Display());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MinimumSelectedColumnOverflowsEvenWithoutSelection(bool selected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = Create(fixture, 10);
        if (selected)
        {
            Select(view, 0);
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        await Assert.ThrowsAsync<OverflowException>(async () =>
            await session.InvokeAsync("GETSLCTLST", [id, N(1), N(int.MinValue)], default));
        Assert.Equal("", (await session.InvokeAsync("GETSLCTLST",
            [id, N(2), N(int.MinValue)], default)).Display());
    }

    [Theory]
    [InlineData(1, 1, "")]
    [InlineData(1, 2, "same")]
    [InlineData(0, 1, "&first...\t")]
    [InlineData(0, 2, "&first...\t\tsame")]
    public async Task SelectedEmptyCellsUseAccumulatedText(int first, int last,
        string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = Create(fixture, 10);
        for (int index = first; index <= last; index++)
        {
            Select(view, index);
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(expected,
            (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
    }

    internal static void Select(nint view, int index)
    {
        var item = new Item { StateMask = 2, State = 2 };
        Assert.NotEqual(0, Send(view, 0x102b, index, item));
    }

    internal static void SetStateImage(nint view, int index, int image)
    {
        var item = new Item { StateMask = 0xf000, State = (uint)(image << 12) };
        Assert.NotEqual(0, Send(view, 0x102b, index, item));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClickOnActivatesWithEnterAndOffOnlySelects(bool on)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        Create(fixture, 10);
        List<int> notices = [];
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            notices.Add(notice.Code);
            return 0;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same"), N(32), N(on ? 1 : 0)], default));
        Assert.Equal(on, notices.Contains(-4));
        Assert.Equal("same", (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ControlKeyReleasesOnCancellationAndCallbackFailure(bool failure)
    {
        using var cancellation = new CancellationTokenSource();
        List<(uint Message, nint Key, nint Parameter)> messages = [];
        Exception? exception = Record.Exception(() => NativeControlKey.Press(42, 13,
            cancellation.Token, (_, message, key, parameter) =>
            {
                messages.Add((message, key, parameter));
                if (message == 0x100)
                {
                    if (failure)
                    {
                        throw new InvalidOperationException("検証用送信失敗");
                    }

                    cancellation.Cancel();
                }

                return 0;
            }));
        (uint, nint, nint)[] expected =
        [
            (0x100, 13, 0), (0x101, 13, unchecked((nint)(int)0xc0000000))
        ];
        Assert.Equal(expected, messages);
        if (failure)
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
        else
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClickSelectsOneNamedRowAndHonorsParentVeto(bool veto)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = Create(fixture, 10);
        Select(view, 0);
        List<int> notices = [];
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            notices.Add(notice.Code);
            return veto && notice.Code == -100 ? 1 : 0;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same"), N(32), N(1), N(2)], default));
        Assert.Contains(-100, notices);
        Assert.Equal(veto ? "&first..." : "same",
            (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
        if (!veto)
        {
            Assert.Equal(3, Native.SendMessageW(view, 0x102c, 3, 3));
            notices.Clear();
            Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("same"), N(32), N(1), N(2)], default));
            Assert.Contains(-100, notices);
            Assert.Contains(-101, notices);
        }

        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task ClickCountsNamesAcrossControlsAndUsesDefaultFallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint first = Create(fixture, 10);
        nint second = Create(fixture, 60);
        Select(first, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same"), N(32), N(1), N(3)], default));
        Assert.Equal(3, Native.SendMessageW(second, 0x102c, 2, 3));
        Assert.Equal("", (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("missing"), N(32)], default));
        Assert.Equal("", (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
        Assert.Equal(0, Native.SendMessageW(second, 0x102c, 2, 3));
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform()))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT CLKITEM(id, "&FIRST...", 0)
                PRINT CLKITEM(id, "same", CLK_LSTVEW OR CLK_BACK)
                PRINT GETSLCTLST(id)
                """, output.Add);
        Assert.Equal(["1", "1", "same"], output);
        Create(fixture, 110, 0x08000001);
        nint hidden = Create(fixture, 150);
        Native.ShowWindow(hidden, 0);
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same"), N(32), N(1), N(5)], default));
    }

    [Fact]
    public async Task ClickContinuesAfterAdditionalSelectionMadeByParent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = Create(fixture, 10);
        Select(view, 4);
        bool changed = false;
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (notice.Code == -101 && !changed)
            {
                changed = true;
                Select(view, 0);
            }

            return 0;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same"), N(32), N(1), N(2)], default));
        Assert.True(changed);
        Assert.Equal(0, Native.SendMessageW(view, 0x102c, 0, 2));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task ClickCancellationLeavesPartialChangesAndRejectsUnsupportedModes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = Create(fixture, 10);
        Select(view, 0);
        Select(view, 3);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        using var cancellation = new CancellationTokenSource();
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (notice.Code == -101)
            {
                cancellation.Cancel();
            }

            return 0;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String("same"), N(32)], cancellation.Token));
        Assert.Equal(0, Native.SendMessageW(view, 0x102c, 0, 2));
        Assert.Equal(2, Native.SendMessageW(view, 0x102c, 3, 2));
        fixture.Notify = null;
        ListViewControlTests.SetText(view, 0, "same\tfirst");
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same\tfirst"), N(32)], default));
        Native.SendMessageW(view, 0x1036, 4, 4);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("same"), N(32)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CHKBTN", [id, Value.String("same")], default));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task SelectedTextUsesMixedControlNumbersAndListViewColumns()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 0, 10);
        Assert.NotEqual(0, Native.SendText(list, 0x180, 0, " list ", 2, 1000, out _));
        Native.SendMessageW(list, 0x186, 0, 0);
        SliderControlTests.InitializeControls(2);
        nint tree = fixture.Child("SysTreeView32", "", 0, 50);
        nint leaf = TreeControlTests.Add(tree, TreeControlTests.Add(tree, 0, " 親 "), " 子 ");
        nint view = Create(fixture, 90);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(" list ", (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
        Assert.Equal("", (await session.InvokeAsync("GETSLCTLST", [id, N(2)], default)).Display());
        Assert.Equal("", (await session.InvokeAsync("GETSLCTLST", [id, N(3)], default)).Display());
        Assert.NotEqual(0, Native.SendMessageW(tree, 0x110b, 9, leaf));
        Select(view, 0);
        Select(view, 1);
        Select(view, 3);
        Assert.Equal(" 親 \\ 子 ", (await session.InvokeAsync("GETSLCTLST", [id, N(2)], default)).Display());
        Assert.Equal("&first...\t\tsame", (await session.InvokeAsync("GETSLCTLST", [id, N(3)], default)).Display());
        Assert.Equal("値0\t値1\t値3", (await session.InvokeAsync("GETSLCTLST", [id, N(3), N(2)], default)).Display());
        Assert.Equal("", (await session.InvokeAsync("GETSLCTLST", [id, N(3), N(999)], default)).Display());
        Assert.Equal(" 親 \\ 子 ",
            (await session.InvokeAsync("GETSLCTLST", [id, N(2), N(1)], default)).Display());
        Assert.Equal("", (await session.InvokeAsync("GETSLCTLST", [id, N(999)], default)).Display());
        await session.InvokeAsync("GETITEM", [id, N(32)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform()))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT GETSLCTLST(id, 2)
                PRINT GETSLCTLST(id, 3, 2)
                """, output.Add);
        Assert.Equal([" 親 \\ 子 ", "値0\t値1\t値3"], output);
        Assert.Same(snapshot, session.AllItems);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETSLCTLST", [id, N(3)], cancellation.Token));
        Assert.Same(snapshot, session.AllItems);
    }

    [Fact]
    public async Task ReadsRowsSubItemsHeadersAndControlNumbers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = Create(fixture, 10);
        Create(fixture, 60, 0x08000001);
        nint hidden = Create(fixture, 110);
        Native.ShowWindow(hidden, 0);
        fixture.Child("Button", "button", 0, 150);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(5), await session.InvokeAsync("GETITEM", [id, N(32)], default));
        Assert.Equal(["&first...", "", "same", "same", new string('長', 255)], session.AllItems);
        Assert.Equal(N(5), await session.InvokeAsync("GETITEM", [id, N(32), N(1), N(1)], default));
        Assert.Equal("&first...", session.AllItems[0]);
        Assert.Equal(N(5), await session.InvokeAsync("GETITEM", [id, N(32), N(1), N(2)], default));
        Assert.Equal(["値0", "値1", "値2", "値3", "値4"], session.AllItems);
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM", [id, N(32), N(2), N(-1)], default));
        Assert.Equal(["Name", "値"], session.AllItems);
        Assert.Equal(N(4), await session.InvokeAsync("GETITEM", [id, N(32), N(-1), N(-1)], default));
        Assert.Equal(N(3), await session.InvokeAsync("GETITEM", [id, N(33), N(-1), N(-1), N(1)], default));
        Assert.Equal(["button", "Name", "値"], session.AllItems);
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(32), N(1), N(3)], default));
        Assert.Empty(session.AllItems);
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(32), N(999)], default));
        Assert.NotEqual(0, Native.SendMessageW(view, 0x101e, 1, 0));
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM", [id, N(32), N(1), N(-1)], default));
        Assert.Equal(["Name", "値"], session.AllItems);
    }

    [Fact]
    public async Task InterpreterAliasesAndFailurePublishSuccessfulPrefix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        Create(fixture, 10);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform()))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                FOR item IN GETITEM(id, ITM_LISTVIEW, , -1)
                    PRINT item
                NEXT
                PRINT GETITEM(id, ITM_LSTVEW)
                PRINT GETITEM(id, ITM_LISTVEW)
                """, output.Add);
        Assert.Equal(["Name", "値", "5", "5"], output);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, N(32)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        fixture.Child("SysListView32", "", 0x1001, 60);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(32), N(-1)], default));
        Assert.NotSame(snapshot, session.AllItems);
        Assert.Equal(snapshot, session.AllItems);
        snapshot = session.AllItems;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(32)], cancellation.Token));
        Assert.Same(snapshot, session.AllItems);
    }
}
