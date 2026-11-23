using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeDirectLabelTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal nint Procedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
        internal nint SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetClassInfoExW(nint instance,
        string name, ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(
        ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string name,
        nint instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [Theory]
    [InlineData("TColorBox", 1)]
    [InlineData("TComboBoxEx", 1)]
    [InlineData("TListBox", 2)]
    [InlineData("SysListView32", 3)]
    [InlineData("TTreeView", 4)]
    [InlineData("ComboBoxListBox", 1)]
    [InlineData(" ListBox\t", 2)]
    [InlineData("Unknown", 0)]
    public void SelectionUsesRecoveredLabelRouting(string label,
        int expected)
    {
        Assert.Equal((NativeSelectionKind)expected, NativeSelectionTarget.Kind(
            DirectTargetSelector.NormalizeLabel(label)));
    }

    [Fact]
    public void NestedComboRequiresEarlierMatchingParent()
    {
        (nint Window, string Label)[] previous = [(12, "COMBOBOXEX32")];
        Assert.True(NativeSelectionTarget.IsNestedCombo("COMBOBOX", 12, previous));
        Assert.False(NativeSelectionTarget.IsNestedCombo("COMBOBOX", 13, previous));
        Assert.False(NativeSelectionTarget.IsNestedCombo("COLORBOX", 12, previous));
        Assert.False(NativeSelectionTarget.IsNestedCombo("COMBOBOXLISTBOX", 12, previous));
        Assert.True(NativeSelectionTarget.IsNestedCombo("COMBOBOXTREEVIEW", 12, previous, listOnly: true));
        Assert.False(NativeSelectionTarget.IsNestedCombo("COMBOBOXTREEVIEW", 12, previous));
        Assert.False(NativeSelectionTarget.IsNestedCombo("COMBOBOX", 12, []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenRootUsesChildVisibleStyle(bool childVisible)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint list = fixture.Child("LISTBOX", "", 0, 10);
        Native.SendText(list, 0x180, 0, "selected", 2, 1000, out _);
        Native.SendMessageW(list, 0x186, 0, 0);
        if (!childVisible)
        {
            Native.ShowWindow(list, 0);
        }

        Native.ShowWindow(fixture.Root, 0);
        Assert.False(Native.IsWindowVisible(list));
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String(childVisible ? "selected" : ""),
            await session.InvokeAsync("GETSLCTLST", [id], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListOnlyFlagsKeepNumbersSeparateFromTreeAndListView(bool hiddenRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(3);
        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child("SysTreeView32", "", 0, 1);
        ListViewControlTests.Create(fixture, 5);
        nint disabled = fixture.Child("LISTBOX", "", 0x08000000, 30);
        nint list = fixture.Child("LISTBOX", "", 0, 60);
        nint combo = fixture.Child("COMBOBOX", "", 3, 100);
        Native.SendText(disabled, 0x180, 0, "disabled", 2, 1000, out _);
        Native.SendText(list, 0x180, 0, "A", 2, 1000, out _);
        Native.SendText(combo, 0x143, 0, "B", 2, 1000, out _);
        if (hiddenRoot)
        {
            Native.ShowWindow(fixture.Root, 0);
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(3), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(2), Value.Numeric(-1)], default));
        Assert.Equal(["disabled", "A", "B"], session.AllItems);
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(2), Value.Numeric(-1), Value.Numeric(0), Value.Numeric(1)], default));
        Assert.Equal(["A", "B"], session.AllItems);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(2), Value.Numeric(2)], default));
        Assert.Equal(["A"], session.AllItems);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("B"), Value.Numeric(2), Value.Numeric(1), Value.Numeric(2)], default));
        Assert.Equal("B", NativeListControl.SelectedText(combo, default));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task ListRoutesRecognizeSuperclassedControls(bool combo, bool disabled,
        bool hiddenRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string name = "UWSCSharpSelection-" + Guid.NewGuid();
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>()
        };
        Assert.True(GetClassInfoExW(0, combo ? "COMBOBOX" : "LISTBOX", ref windowClass));
        nint instance = GetModuleHandleW(null);
        nint className = Marshal.StringToHGlobalUni(name);
        bool registered = false;
        try
        {
            windowClass.Instance = instance;
            windowClass.ClassName = className;
            windowClass.Style &= ~0x4000u;
            Assert.NotEqual((ushort)0, RegisterClassExW(ref windowClass));
            registered = true;
            using var fixture = new ButtonControlTests.Fixture();
            nint child = Native.CreateWindowExW(0, name, "",
                0x50000000u | (combo ? 3u : 0u) | (disabled ? 0x08000000u : 0u),
                10, 10, 150, 100,
                fixture.Root, 42, instance, 0);
            Assert.NotEqual(0, child);
            Assert.Equal(combo ? "COMBOBOX" : "LISTBOX",
                NativeSelectionTarget.Label(child));
            Assert.NotEqual(0, Native.SendText(child, combo ? 0x143u : 0x180u,
                0, " 日本語 ", 2, 1000, out _));
            Native.SendMessageW(child, combo ? 0x14eu : 0x186u, 0, 0);
            if (hiddenRoot)
            {
                Native.ShowWindow(fixture.Root, 0);
            }

            await using IWindowsAutomationSession session = fixture.Session();
            Value id = await session.InvokeAsync("HNDTOID",
                [Value.Numeric((long)fixture.Root)], default);
            Assert.Equal(Value.String(disabled ? "" : " 日本語 "),
                await session.InvokeAsync("GETSLCTLST", [id], default));
            Assert.Equal(Value.Numeric(1),
                await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
            Assert.Equal(["日本語"], session.AllItems);
            if (disabled)
            {
                Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM",
                    [id, Value.Numeric(2), Value.Numeric(1), Value.Numeric(0), Value.Numeric(1)], default));
            }

            Native.SendMessageW(child, combo ? 0x14eu : 0x186u, -1, 0);
            Assert.Equal(Value.Numeric(disabled ? 0 : 1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("日本語"), Value.Numeric(2)], default));
            Assert.Equal(Value.String(disabled ? "" : " 日本語 "),
                await session.InvokeAsync("GETSLCTLST", [id], default));
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            if (registered)
            {
                Assert.True(UnregisterClassW(name, instance));
            }

            Marshal.FreeHGlobal(className);
        }
    }

    [Fact]
    public void SelectorRecognizesSuperclassedEditByUnderlyingType()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string name = "UWSCSharpText-" + Guid.NewGuid();
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>()
        };
        Assert.True(GetClassInfoExW(0, "EDIT", ref windowClass));
        nint instance = GetModuleHandleW(null);
        Assert.NotEqual(0, instance);
        nint className = Marshal.StringToHGlobalUni(name);
        nint root = 0;
        bool registered = false;
        try
        {
            // EDITの元手続きを使う専用クラスだけを、このテスト内で登録します。
            windowClass.Instance = instance;
            windowClass.ClassName = className;
            windowClass.Style &= ~0x4000u;
            Assert.NotEqual((ushort)0, RegisterClassExW(ref windowClass));
            registered = true;
            root = Native.CreateWindowExW(0, "STATIC", name,
                0, 30, 40, 240, 160, 0, 0, 0, 0);
            Assert.NotEqual(0, root);
            nint child = Native.CreateWindowExW(0, name, "",
                0x50000000, 10, 20, 100, 30, root, 0, instance, 0);
            Assert.NotEqual(0, child);
            var buffer = new StringBuilder(256);
            Native.GetClassNameW(child, buffer, buffer.Capacity);
            Assert.Equal(name, buffer.ToString());
            buffer.Clear();
            Assert.NotEqual(0u, Native.RealGetWindowClassW(child,
                buffer, (uint)buffer.Capacity));
            Assert.Equal("EDIT",
                DirectTargetSelector.NormalizeLabel(buffer.ToString()));
            var backend = new NativeDirectInputBackend();
            Assert.Equal(child, backend.SelectKeyboard(root, null));
        }
        finally
        {
            if (root != 0)
            {
                Native.DestroyWindow(root);
            }

            if (registered)
            {
                UnregisterClassW(name, instance);
            }

            Marshal.FreeHGlobal(className);
        }
    }
}
