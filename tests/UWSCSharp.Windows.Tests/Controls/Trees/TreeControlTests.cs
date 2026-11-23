using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class TreeControlTests
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Item
    {
        public uint Mask;
        public nint Handle;
        public uint State;
        public uint StateMask;
        public nint Text;
        public int Capacity;
        public int Image;
        public int SelectedImage;
        public int Children;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Insert
    {
        public nint Parent;
        public nint After;
        public Item Item;
    }

    internal static nint Add(nint tree, nint parent, string name)
    {
        nint text = Marshal.StringToHGlobalUni(name);
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Insert>());
        try
        {
            var insert = new Insert
            {
                Parent = parent == 0 ? -65536 : parent,
                After = -65534,
                Item = new Item { Mask = 1, Text = text }
            };
            Marshal.StructureToPtr(insert, pointer, false);
            nint result = Native.SendMessageW(tree, 0x1132, 0, pointer);
            Assert.NotEqual(0, result);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            Marshal.FreeHGlobal(text);
        }
    }

    private static Value N(long value) => Value.Numeric(value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClickReportsMatchEvenWhenParentVetoesSelection(bool veto)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint first = Add(tree, Add(tree, 0, "root"), " &target... ");
        nint duplicate = Add(tree, Add(tree, 0, "other"), "target");
        List<int> notices = [];
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            notices.Add(notice.Code);
            return veto && notice.Code == -450 ? 1 : 0;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), N(16), N(1), N(1)], default));
        Assert.Equal(veto ? 0 : duplicate, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.NotEqual(first, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Contains(-450, notices);
        Assert.Equal(!veto, notices.Contains(-451));
        if (!veto)
        {
            Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("\\other\\target"), N(16)], default));
            Assert.Equal(duplicate, Native.SendMessageW(tree, 0x110a, 9, 0));
        }

        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("missing"), N(16 | 536870912)], default));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task ClickDefaultFallbackAndNotificationCancellation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        Add(tree, 0, "first");
        nint second = Add(tree, 0, "second");
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("first"), N(0)], default));
        using var cancellation = new CancellationTokenSource();
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (notice.Code == -450)
            {
                cancellation.Cancel();
            }

            return 0;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String("second"), N(16)], cancellation.Token));
        Assert.Equal(second, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.Null(fixture.CallbackFailure);
        fixture.Notify = null;
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform()))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT CLKITEM(id, "first", CLK_TREEVEW OR CLK_BACK)
                PRINT GETSLCTLST(id)
                """, output.Add);
        Assert.Equal(["1", "first"], output);
        nint disabled = fixture.Child("SysTreeView32", "", 0x08000000, 60);
        Add(disabled, 0, "disabled");
        nint hidden = fixture.Child("SysTreeView32", "", 0, 90);
        Add(hidden, 0, "hidden");
        Native.ShowWindow(hidden, 0);
        foreach (string name in new[] { "disabled", "hidden" })
        {
            Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
                [id, Value.String(name), N(16 | 536870912)], default));
        }

        nint check = fixture.Child("SysTreeView32", "", 0x100, 50);
        Add(check, 0, "check");
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("check"), N(16)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CHKBTN", [id, Value.String("check")], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedSelectionReportsMatchDespitePersistentRedirect(bool persistent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint first = Add(tree, 0, "first");
        nint second = Add(tree, 0, "second");
        using var hook = new ToolbarClickTextTests.Interceptor(tree, (_, _, _) => null,
            after: (message, _, pointer) =>
            {
                if (message == 0x113e && Marshal.ReadInt32(pointer) == 0x41)
                {
                    // 展開分岐を固定し、native handle上位の偶然の値に依存させません。
                    Marshal.WriteInt32(pointer, IntPtr.Size == 8 ? 12 : 8, 0);
                }
            });
        bool changed = false;
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (notice.Code == -451 && (!changed || persistent)
                && Native.SendMessageW(tree, 0x110a, 9, 0) != second)
            {
                changed = true;
                Native.SendMessageW(tree, 0x110b, 9, second);
            }

            return 0;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("first"), N(16)], default));
        Assert.Equal(persistent ? second : first, Native.SendMessageW(tree, 0x110a, 9, 0));
        Assert.True(changed);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task EnumeratesCollapsedTreePathsAndSelectsControlNumber()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint root = Add(tree, 0, " ROOT ");
        nint child = Add(tree, root, " &child... ");
        Add(tree, child, "日本語");
        Add(tree, root, "");
        Add(tree, 0, "ROOT");
        Add(tree, 0, new string('長', 300));
        nint second = fixture.Child("SysTreeView32", "", 0x08000000, 60);
        Add(second, 0, "disabled");
        nint hidden = fixture.Child("SysTreeView32", "", 0, 110);
        Add(hidden, 0, "hidden");
        Native.ShowWindow(hidden, 0);
        fixture.Child("Button", "button", 0, 150);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(6), await session.InvokeAsync("GETITEM", [id, N(16)], default));
        Assert.Equal(["ROOT", "ROOT\\&child...", "ROOT\\&child...\\日本語",
            "ROOT\\", "ROOT", new string('長', 255)], session.AllItems);
        IReadOnlyList<string> first = session.AllItems;
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM", [id, N(16), N(2)], default));
        Assert.Equal(["disabled"], session.AllItems);
        Assert.Equal(N(7), await session.InvokeAsync("GETITEM", [id, N(16), N(-1)], default));
        Assert.Equal(N(7), await session.InvokeAsync("GETITEM", [id, N(17), N(-1), N(0), N(1)], default));
        Assert.Equal("button", session.AllItems[0]);
        Assert.DoesNotContain("disabled", session.AllItems);
        Assert.Equal(6, first.Count);
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(16), N(999)], default));
        Assert.Empty(session.AllItems);
    }

    [Fact]
    public async Task InterpreterPublishesPathsAndCancellationPreservesSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        Add(tree, Add(tree, 0, "親"), "子");
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform()))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                FOR item IN GETITEM(id, ITM_TREEVIEW)
                    PRINT item
                NEXT
                PRINT ALL_ITEM_LIST[1]
                PRINT GETITEM(id, ITM_TREEVEW)
                """, output.Add);
        Assert.Equal(["親", "親\\子", "親\\子", "2"], output);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, N(16)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(16)], cancellation.Token));
        Assert.Same(snapshot, session.AllItems);
        Assert.True(Native.DestroyWindow(tree));
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(16)], default));
        Assert.Empty(session.AllItems);
    }

    [Fact]
    public async Task RejectsExcessiveDepthAfterPublishingSuccessfulPrefix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint tree = fixture.Child("SysTreeView32", "", 0, 10);
        nint item = 0;
        for (int index = 0; index < 260; index++)
        {
            item = Add(tree, item, "a");
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        IReadOnlyList<string> before = session.AllItems;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(16)], default));
        Assert.NotSame(before, session.AllItems);
        Assert.Equal(257, session.AllItems.Count);
    }
}
