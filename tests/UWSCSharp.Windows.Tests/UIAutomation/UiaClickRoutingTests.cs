using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed partial class UiaSelectionTests
{
    [Theory]
    [InlineData("Chrome_WidgetWin_UWSC_API_")]
    [InlineData("MozillaUWSC_API_")]
    public async Task ExplicitApiDoesNotUsePreferredUiaRoot(string prefix)
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
                int notifications = 0;
                nint check = 0;
                using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
                {
                    host.Child("BUTTON", "opaque", 11, 10);
                    check = host.Child("BUTTON", "check", 6, 40);
                    Native.SendMessageW(check, 0xf1, 1, 0);
                    host.Clicked = (_, _) => Interlocked.Increment(ref notifications);
                }, rootClass: className);
                await using IWindowsAutomationSession session = new WindowsAutomation(
                    _ => throw new InvalidOperationException("物理入力は禁止です。"),
                    listSelectionPlatform: new OwnedListSelectionPlatform(),
                    buttonAccessibleFactory: _ => throw new InvalidOperationException("ACCは禁止です。"))
                    .CreateSession();
                Value id = await session.InvokeAsync("HNDTOID",
                    [Value.Numeric((long)fixture.Root)], default);
                Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                    [id, Value.String("opaque"), Value.Numeric(1 | 512 | 1073741824)], default));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref notifications) == 1,
                    TimeSpan.FromSeconds(5)));
                notifications = 0;
                Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                    [id, Value.String("opaque"), Value.Numeric(1 | 512 | 536870912)], default));
                Assert.Equal(0, Volatile.Read(ref notifications));
                Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                    [id, Value.String("check"), Value.Numeric(1 | 512), Value.Numeric(0)], default));
                Assert.Equal((nint)0, Native.SendMessageW(check, 0xf0, 0, 0));
                Assert.Equal(2, Volatile.Read(ref notifications));
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
    [InlineData("SysTabControl32", 8)]
    [InlineData("SysTreeView32", 16)]
    [InlineData("SysListView32", 32)]
    [InlineData("ToolbarWindow32", 64)]
    public async Task KnownClickAliasesUseNativeOperations(string standard, int kind)
    {
        SliderControlTests.InitializeControls(0xff);
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
                nint control = 0;
                string caption = kind == 32 ? "same" : "target";
                int notifications = 0;
                using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
                {
                    switch (kind)
                    {
                        case 8:
                            control = TabControlTests.CreateNamed(host, className, 10, 0, "first", caption);
                            break;
                        case 16:
                            control = host.Child(className, "", 0, 10);
                            TreeControlTests.Add(control, 0, caption);
                            break;
                        case 32:
                            control = ListViewControlTests.Create(host, 10, className: className);
                            break;
                        case 64:
                            control = ToolbarControlTests.Create(host, 10, className: className);
                            ToolbarControlTests.Add(control, caption, 701);
                            break;
                    }

                    host.Notify = (_, _) =>
                    {
                        Interlocked.Increment(ref notifications);
                        return 0;
                    };
                });
                await using IWindowsAutomationSession session = new WindowsAutomation(_ =>
                    throw new InvalidOperationException("物理入力は禁止です。"),
                    listSelectionPlatform: new OwnedListSelectionPlatform()).CreateSession();
                Value id = await session.InvokeAsync("HNDTOID",
                    [Value.Numeric((long)fixture.Root)], default);
                Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                    [id, Value.String(caption), Value.Numeric(kind | 536871424)], default)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                if (kind == 8)
                {
                    Assert.Equal((nint)1, Native.SendMessageW(control, 0x130b, 0, 0));
                    Assert.True(Volatile.Read(ref notifications) > 0);
                }
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
