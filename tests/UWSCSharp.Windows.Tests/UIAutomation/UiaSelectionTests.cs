using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class UiaSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledCandidatesStillConsumeTypeOrderedOccurrence(bool disableRoot)
    {
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            nint disabled = host.Child("LISTBOX", "", 0x08000000, 10);
            nint enabled = host.Child("LISTBOX", "", 0, 50);
            Assert.NotEqual(0, Native.SendText(disabled, 0x180, 0, "disabled", 2, 1000, out _));
            Assert.NotEqual(0, Native.SendText(enabled, 0x180, 0, "enabled", 2, 1000, out _));
            Native.SendMessageW(disabled, 0x186, 0, 0);
            Native.SendMessageW(enabled, 0x186, 0, 0);
            nint combo = host.Child("COMBOBOX", "", 0x08000003, 90, height: 100);
            Assert.NotEqual(0, Native.SendText(combo, 0x143, 0, "combo", 2, 1000, out _));
            Native.SendMessageW(combo, 0x14e, 0, 0);
            SliderControlTests.InitializeControls(2);
            nint tree = host.Child("SysTreeView32", "", 0x08000000, 130);
            Native.SendMessageW(tree, 0x110b, 9, TreeControlTests.Add(tree, 0, "tree"));
            if (disableRoot)
            {
                EnableWindow(host.Root, false);
            }
        });
        Assert.Equal("disabled", await UiaSelection.ReadAsync(fixture.Root, 1, 0, default));
        Assert.Equal("enabled", await UiaSelection.ReadAsync(fixture.Root, 2, 0, default));
        Assert.Equal("combo", await UiaSelection.ReadAsync(fixture.Root, 3, 0, default));
        Assert.Equal("\\tree", await UiaSelection.ReadAsync(fixture.Root, 4, 0, default));
        Assert.Equal("", await UiaSelection.ReadAsync(fixture.Root, 5, 0, default));
        foreach (int column in new[] { -1, int.MinValue })
        {
            Assert.Equal("disabled", await UiaSelection.ReadAsync(fixture.Root, 1, column, default));
            Assert.Equal("\\tree", await UiaSelection.ReadAsync(fixture.Root, 4, column, default));
            Assert.Equal("combo", await UiaSelection.ReadAsync(fixture.Root, 3, column, default));
        }
    }

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(nint window, bool enabled);

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
    private static extern bool GetClassInfoExW(nint instance, string name, ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string name, nint instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [Theory]
    [InlineData("Chrome_WidgetWin_UWSC_")]
    [InlineData("MozillaUWSC_")]
    public async Task PreferredRootRoutesScriptToUiaCandidateNumbering(string prefix)
    {
        string className = prefix + Guid.NewGuid().ToString("N");
        var registered = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>() };
        Assert.True(GetClassInfoExW(0, "STATIC", ref registered));
        nint name = Marshal.StringToHGlobalUni(className);
        nint instance = GetModuleHandleW(null);
        try
        {
            registered.ClassName = name;
            registered.Instance = instance;
            registered.Style |= 0x4000;
            Assert.NotEqual((ushort)0, RegisterClassExW(ref registered));
            try
            {
                using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
                {
                    nint disabled = host.Child("LISTBOX", "", 0x08000000, 10);
                    Assert.NotEqual(0, Native.SendText(disabled, 0x180, 0, "disabled", 2, 1000, out _));
                    Native.SendMessageW(disabled, 0x186, 0, 0);
                    nint list = host.Child("LISTBOX", "", 0, 90);
                    Assert.NotEqual(0, Native.SendText(list, 0x180, 0, "enabled", 2, 1000, out _));
                    Native.SendMessageW(list, 0x186, 0, 0);
                    host.Child("STATIC", " label ", 0, 130);
                    host.Child("BUTTON", " &button... ", 0, 170);
                }, rootClass: className);
                List<string> output = [];
                await new Interpreter(windows: new WindowsAutomation(_ =>
                    throw new InvalidOperationException("物理入力は禁止です。"))).RunAsync(
                    $"id=HNDTOID({(long)fixture.Root})\nPRINT GETSLCTLST(id)\nPRINT GETSLCTLST(id,2)", output.Add);
                Assert.Equal(["disabled", "enabled"], output);
                output.Clear();
                await new Interpreter(windows: new WindowsAutomation(_ =>
                    throw new InvalidOperationException("物理入力は禁止です。"))).RunAsync(
                    $"id=HNDTOID({(long)fixture.Root})\nGETITEM(id,ITM_STATIC)\nPRINT ALL_ITEM_LIST[0]\nGETITEM(id,ITM_BTN+ITM_STATIC)\nFOR item IN ALL_ITEM_LIST\nPRINT item\nNEXT",
                    output.Add);
                Assert.Equal(["label", "button", "label"], output);
            }
            finally
            {
                Assert.True(UnregisterClassW(className, instance));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadsOwnedComboTreeAndListProxy(int kind)
    {
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            if (kind == 0)
            {
                nint combo = host.Child("COMBOBOX", "", 3, 10, height: 100);
                Assert.NotEqual(0, Native.SendText(combo, 0x143, 0, "日本語", 2, 1000, out _));
                Native.SendMessageW(combo, 0x14e, 0, 0);
            }
            else if (kind == 1)
            {
                SliderControlTests.InitializeControls(2);
                nint tree = host.Child("SysTreeView32", "", 0, 10, height: 100);
                nint item = TreeControlTests.Add(tree, 0, "日本語");
                Native.SendMessageW(tree, 0x110b, 9, item);
            }
            else
            {
                nint view = ListViewControlTests.Create(host, 10);
                ListViewControlTests.Select(view, 0);
            }
        });
        string selected = await UiaSelection.ReadAsync(fixture.Root, 1, 0, default);
        Assert.Equal(kind == 2 ? " &first... " : kind == 1 ? "\\日本語" : "日本語", selected);
        if (kind == 2)
        {
            Assert.Equal(" &first... ", await UiaSelection.ReadAsync(fixture.Root, 1, 2, default));
        }
    }

    [Fact]
    public async Task MixedControlTypesConsumeOccurrenceByTypeBeforeGeometry()
    {
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            nint combo = host.Child("COMBOBOX", "", 3, 10, height: 100);
            Assert.NotEqual(0, Native.SendText(combo, 0x143, 0, "combo", 2, 1000, out _));
            Native.SendMessageW(combo, 0x14e, 0, 0);
            SliderControlTests.InitializeControls(2);
            nint tree = host.Child("SysTreeView32", "", 0, 50);
            Native.SendMessageW(tree, 0x110b, 9, TreeControlTests.Add(tree, 0, "tree"));
            nint list = host.Child("LISTBOX", "", 0, 90);
            Assert.NotEqual(0, Native.SendText(list, 0x180, 0, "list", 2, 1000, out _));
            Native.SendMessageW(list, 0x186, 0, 0);
            nint view = ListViewControlTests.Create(host, 130);
            ListViewControlTests.Select(view, 0);
        });
        Assert.Equal("list", await UiaSelection.ReadAsync(fixture.Root, 1, 0, default));
        Assert.Equal(" &first... ", await UiaSelection.ReadAsync(fixture.Root, 2, 0, default));
        Assert.Equal("\\tree", await UiaSelection.ReadAsync(fixture.Root, 4, 0, default));
        Assert.Equal("", await UiaSelection.ReadAsync(fixture.Root, 5, 0, default));
        Assert.Equal("combo", await UiaSelection.ReadAsync(fixture.Root, 3, 0, default));
    }

    [Theory]
    [InlineData("HwndWrapper[WPFApplication;test", true)]
    [InlineData("ApplicationFrameWindow", true)]
    [InlineData("IEFrame", true)]
    [InlineData("Chrome_WidgetWin_1", true)]
    [InlineData("MozillaWindowClass", true)]
    [InlineData("UWSCFixture", false)]
    [InlineData("prefixChrome_WidgetWin", false)]
    public void RoutesOnlyPreferredRootClasses(string name, bool expected) =>
        Assert.Equal(expected, UiaSelection.PrefersUia(name));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ReadsRawMultipleSelectionFromOwnedList(int occurrence)
    {
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            nint list = host.Child("LISTBOX", "", 8, 10);
            foreach (string text in new[] { "日本語", "&second...", "third" })
            {
                nint memory = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(text);
                try
                {
                    Native.SendMessageW(list, 0x180, 0, memory);
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);
                }
            }

            Native.SendMessageW(list, 0x185, 1, 0);
            Native.SendMessageW(list, 0x185, 1, 2);
        });
        Assert.Equal("日本語\tthird", await UiaSelection.ReadAsync(
            fixture.Root, occurrence, 0, default));
        Assert.Equal("", await UiaSelection.ReadAsync(fixture.Root, 2, 0, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await UiaSelection.ReadAsync(fixture.Root, 1, 0, cancellation.Token));
    }
}
