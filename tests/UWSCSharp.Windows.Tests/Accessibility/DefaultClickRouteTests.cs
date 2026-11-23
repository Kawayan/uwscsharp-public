using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class DefaultClickRouteTests
{
    [Theory]
    [InlineData(2, 1)]
    [InlineData(16386, 0)]
    public async Task OfThisDoesNotReinterpretOrdinalThroughGenericMsaa(int flags, int expected)
    {
        int actions = 0;
        var item = new AccessibleTestProvider(34, 0, "target", "")
        {
            TakingFocus = _ => { },
            PerformingAction = () => Interlocked.Increment(ref actions)
        };
        var provider = new AccessibleTestProvider(10, 0, "", "", item);
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(expected), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(flags | 512)], default));
        Assert.Equal(expected, Volatile.Read(ref actions));
    }

    private sealed class FailingChildrenProvider(int code)
        : AccessibleTestProvider(51, 0, "other", ""), IAccessibleNative
    {
        int IAccessibleNative.ChildCount() =>
            throw new COMException("子列挙の失敗", code);
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005))]
    [InlineData(unchecked((int)0x80070005))]
    public async Task DefaultSearchReportsProviderFailureInsteadOfMissing(int code)
    {
        var provider = new FailingChildrenProvider(code);
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                TRY
                    PRINT CLKITEM(id, "missing", CLK_BACK)
                EXCEPT
                    PRINT "caught"
                    PRINT TRY_ERRMSG
                ENDTRY
                PRINT "after"
                """, output.Add);
        Assert.Equal(3, output.Count);
        Assert.Equal("caught", output[0]);
        Assert.Contains("FucCLKITEM:", output[1]);
        Assert.Equal("after", output[2]);
    }

    [Theory]
    [InlineData("COMBOBOX", 3, 2)]
    [InlineData("SysTreeView32", 0, 16)]
    public async Task MissingItemOnPumpedOwnerReturnsFalse(
        string controlClass, int style, int kind)
    {
        SliderControlTests.InitializeControls(2);
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            nint control = host.Child(controlClass, "", style, 10);
            if (kind == 2)
            {
                Native.SendText(control, 0x143, 0, "first", 2, 1000, out _);
            }
            else
            {
                TreeControlTests.Add(control, 0, "first");
            }
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            listSelectionPlatform: new OwnedListSelectionPlatform()).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("missing"), Value.Numeric(kind | 512)], default));
    }

    [Theory]
    [InlineData("LISTBOX", 1, 2)]
    [InlineData("COMBOBOX", 3, 2)]
    [InlineData("SysTreeView32", 0, 16)]
    public async Task MissingNativeItemReturnsFalseAfterProviderSearch(
        string controlClass, int style, int kind)
    {
        using var styles = new VisualStylesContext();
        SliderControlTests.InitializeControls(2);
        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(controlClass, "", style, 10);
        if (kind == 2)
        {
            Native.SendText(control, controlClass == "COMBOBOX" ? 0x143u : 0x180u,
                0, "first", 2, 1000, out _);
        }
        else
        {
            TreeControlTests.Add(control, 0, "first");
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        try
        {
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                [id, Value.String("missing"), Value.Numeric(kind | 512)], default));
        }
        catch (Exception failure)
        {
            Assert.Fail($"通常探索失敗: {failure.GetType().Name}, HRESULT=0x{failure.HResult:X8}, {failure}");
        }
    }

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object accessible);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(536870912, 0)]
    [InlineData(1073741824, 0)]
    [InlineData(32768, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(4, 0)]
    [InlineData(8, 0)]
    [InlineData(16, 0)]
    [InlineData(32, 0)]
    [InlineData(64, 0)]
    public async Task DefaultCanReachMsaaButtonWhileExplicitRoutesStaySeparate(
        int route, int expected)
    {
        int actions = 0;
        var provider = new AccessibleTestProvider(43, 0, "target", "")
        {
            TakingFocus = _ => { },
            PerformingAction = () => Interlocked.Increment(ref actions)
        };
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(expected), await session.InvokeAsync("CLKITEM",
            [id, Value.String("target"), Value.Numeric(route | 512)], default));
        Assert.Equal(expected, Volatile.Read(ref actions));
    }
}
