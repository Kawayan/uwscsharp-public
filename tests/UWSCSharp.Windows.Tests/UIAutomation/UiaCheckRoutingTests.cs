using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class UiaSelectionTests
{
    [DllImport("oleacc.dll", EntryPoint = "LresultFromObject")]
    private static extern nint ReturnCheckProvider(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object provider);

    [Theory]
    [InlineData(19, 1)]
    [InlineData(20, -1)]
    public async Task KnownNativeAliasesParticipateInCheckFallbackThreshold(int count, int expected)
    {
        string className = "WindowsForms10.Button.app.0." + Guid.NewGuid().ToString("N");
        var registered = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>() };
        Assert.True(GetClassInfoExW(0, "BUTTON", ref registered));
        nint name = Marshal.StringToHGlobalUni(className);
        nint instance = GetModuleHandleW(null);
        try
        {
            registered.ClassName = name;
            registered.Instance = instance;
            registered.Style &= ~0x4000u;
            Assert.NotEqual((ushort)0, RegisterClassExW(ref registered));
            try
            {
                using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
                {
                    for (int index = 0; index < count; index++)
                    {
                        host.Child(className, "native" + index, 0, 10);
                    }
                    host.AccessibleObject = parameter =>
                    {
                        Guid iid = typeof(IAccessibleNative).GUID;
                        var provider = new AccessibleTestProvider(44, 0x10, "virtual", "")
                        {
                            ActionName = "Check"
                        };
                        return ReturnCheckProvider(ref iid, (nuint)parameter, provider);
                    };
                }, initializeOle: true);
                Assert.Equal(1, await UiaControl.ReadCheckAsync(fixture.Root, "virtual", 1, default));
                await using IWindowsAutomationSession session = new WindowsAutomation(_ =>
                    throw new InvalidOperationException("物理入力は禁止です。"),
                    listSelectionPlatform: new OwnedListSelectionPlatform()).CreateSession();
                Value id = await session.InvokeAsync("HNDTOID",
                    [Value.Numeric((long)fixture.Root)], default);
                Assert.Equal(Value.Numeric(expected), await session.InvokeAsync("CHKBTN",
                    [id, Value.String("virtual")], default));
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownCheckAliasesReadStateAndKeepSnapshot(bool view)
    {
        string standard = view ? "SysListView32" : "SysTreeView32";
        SliderControlTests.InitializeControls(view ? 1u : 2u);
        string className = "WindowsForms10." + standard + ".app.0." + Guid.NewGuid().ToString("N");
        var registered = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>() };
        Assert.True(GetClassInfoExW(0, standard, ref registered));
        nint name = Marshal.StringToHGlobalUni(className);
        nint instance = GetModuleHandleW(null);
        try
        {
            registered.ClassName = name;
            registered.Instance = instance;
            registered.Style &= ~0x4000u;
            Assert.NotEqual((ushort)0, RegisterClassExW(ref registered));
            try
            {
                nint targetControl = 0;
                nint targetItem = 0;
                using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
                {
                    if (view)
                    {
                        nint control = ListViewControlTests.Create(host, 10, className: className);
                        Native.SendMessageW(control, 0x1036, 4, 4);
                        ListViewControlTests.SetStateImage(control, 2, 2);
                    }
                    else
                    {
                        nint control = host.Child(className, "", 0x100, 10);
                        nint parent = TreeControlTests.Add(control, 0, "parent");
                        nint child = TreeControlTests.Add(control, parent, "child");
                        Native.SendMessageW(control, 0x1102, 2, parent);
                        ViewCheckTests.SetState(control, child, false, 2);
                        targetControl = control;
                        targetItem = child;
                    }
                });
                await using IWindowsAutomationSession session = new WindowsAutomation(_ =>
                    throw new InvalidOperationException("物理入力は禁止です。"),
                    listSelectionPlatform: new OwnedListSelectionPlatform()).CreateSession();
                Value id = await session.InvokeAsync("HNDTOID",
                    [Value.Numeric((long)fixture.Root)], default);
                if (!view)
                {
                    // 最初の描画で行われるチェック画像の初期化後に状態を設定します。
                    Native.SendMessageW(targetControl, 0x000f, 0, 0);
                    ViewCheckTests.SetState(targetControl, targetItem, false, 2);
                    Assert.Equal((nint)0x2000, Native.SendMessageW(
                        targetControl, 0x1127, targetItem, 0xf000));
                }
                await session.InvokeAsync("GETITEM", [id, Value.Numeric(view ? 32 : 16)], default);
                IReadOnlyList<string> snapshot = session.AllItems;
                Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CHKBTN",
                    [id, Value.String(view ? "same" : "parent\\child")], default));
                Assert.Same(snapshot, session.AllItems);
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
    [InlineData("Chrome_WidgetWin_Check_")]
    [InlineData("MozillaCheck_")]
    public async Task PreferredCheckRootSkipsNativeDisabledOrdinalAndThreshold(string prefix)
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
            Assert.NotEqual((ushort)0, RegisterClassExW(ref registered));
            try
            {
                int clicks = 0;
                using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
                {
                    host.Clicked = (_, _) => Interlocked.Increment(ref clicks);
                    nint disabled = host.Child("BUTTON", "same", 0x08000005, 10);
                    nint enabled = host.Child("BUTTON", "same", 5, 50);
                    Native.SendMessageW(disabled, 0xf1, 0, 0);
                    Native.SendMessageW(enabled, 0xf1, 2, 0);
                    for (int index = 0; index < 20; index++)
                    {
                        host.Child("BUTTON", "filler" + index, 0, 100 + index * 5);
                    }
                }, rootClass: className);
                await using IWindowsAutomationSession session = new WindowsAutomation(_ =>
                    throw new InvalidOperationException("物理入力は禁止です。"))
                    .CreateSession();
                Value id = await session.InvokeAsync("HNDTOID",
                    [Value.Numeric((long)fixture.Root)], default);
                await session.InvokeAsync("GETITEM", [id, Value.Numeric(1)], default);
                IReadOnlyList<string> snapshot = session.AllItems;
                foreach (int occurrence in new[] { 0, 1, -1, int.MinValue })
                {
                    Assert.Equal(Value.Numeric(2), await session.InvokeAsync("CHKBTN",
                        [id, Value.String("same"), Value.Numeric(occurrence)], default));
                }
                Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
                    [id, Value.String("same"), Value.Numeric(2)], default));
                Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
                    [id, Value.String("missing")], default));
                Assert.Same(snapshot, session.AllItems);
                Assert.Equal(0, Volatile.Read(ref clicks));
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
}
