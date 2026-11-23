using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class UiaCheckTests
{
    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object provider);

    [Fact]
    public async Task UiaTypesShareOrdinalAndNativeMatchKeepsPriority()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int clicks = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Clicked = (_, _) => Interlocked.Increment(ref clicks);
            nint radio = host.Child("Button", "State", 4, 10);
            nint check = host.Child("Button", "State", 5, 40);
            host.Child("Button", "State", 0, 70);
            Native.SendMessageW(radio, 0xf1, 1, 0);
            Native.SendMessageW(check, 0xf1, 2, 0);
        });
        Assert.Equal(0, await UiaControl.ReadCheckAsync(fixture.Root, "State", 1, default));
        Assert.Equal(2, await UiaControl.ReadCheckAsync(fixture.Root, "State", 2, default));
        Assert.Equal(1, await UiaControl.ReadCheckAsync(fixture.Root, "State", 3, default));
        Assert.Equal(-1, await UiaControl.ReadCheckAsync(fixture.Root, "State", 4, default));
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CHKBTN", [id, Value.String("State")], default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CHKBTN", [id, Value.String("missing")], cancellation.Token));
        fixture.Dispose();
        Assert.Equal(0, Volatile.Read(ref clicks));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(1)]
    public async Task DefaultCheckClampsNonpositiveOrdinalBeforeNativeAndUia(int number)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int clicks = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Clicked = (_, _) => Interlocked.Increment(ref clicks);
            nint first = host.Child("Button", "native", 5, 10);
            host.Child("Button", "native", 5, 40);
            Native.SendMessageW(first, 0xf1, 2, 0);
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var provider = new AccessibleTestProvider(44, 0x10, "virtual", "")
                {
                    ActionName = "Check"
                };
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"));
        await using IWindowsAutomationSession session = automation.CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, Value.Numeric(1)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("CHKBTN",
            [id, Value.String("native"), Value.Numeric(number)], default));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("virtual"), Value.Numeric(number)], default));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CHKBTN",
            [id, Value.String("native"), Value.Numeric(2)], default));
        Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("missing"), Value.Numeric(number)], default));
        Assert.Same(snapshot, session.AllItems);
        List<string> printed = [];
        await new Interpreter(windows: automation).RunAsync(
            $"PRINT CHKBTN(HNDTOID({(long)fixture.Root}), \"native\", {number})", printed.Add);
        Assert.Equal(["2"], printed);
        fixture.Dispose();
        Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("native"), Value.Numeric(number)], default));
        Assert.Equal(0, Volatile.Read(ref clicks));
    }

    [Theory]
    [InlineData(44, 0, 0)]
    [InlineData(44, 0x10, 1)]
    [InlineData(44, 0x20, 2)]
    [InlineData(44, 1, -1)]
    [InlineData(45, 2, 0)]
    [InlineData(45, 0x10, 1)]
    [InlineData(45, 0, 0)]
    [InlineData(43, 0, 0)]
    public async Task UiaStateReaderReadsOwnedMsaaProxyWithoutActions(int role, int state, int expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("Button", "snapshot", 0, 10);
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var provider = new AccessibleTestProvider(role, state, " &State... ", "")
                {
                    ActionName = "Check"
                };
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        Assert.Equal(expected, await UiaControl.ReadCheckAsync(fixture.Root, "state", 1, default));
        Assert.Equal(-1, await UiaControl.ReadCheckAsync(fixture.Root, "missing", 1, default));
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, Value.Numeric(1)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal(Value.Numeric(expected), await session.InvokeAsync("CHKBTN",
            [id, Value.String("state")], default));
        Assert.Same(snapshot, session.AllItems);
        Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("missing")], default));
        fixture.Dispose();
        Assert.Equal(Value.Numeric(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("state")], default));
        Assert.Same(snapshot, session.AllItems);
    }

    [Theory]
    [InlineData(19, 1)]
    [InlineData(20, -1)]
    public async Task DefaultFallbackUsesNativeCandidateThreshold(int nativeCount, int expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            for (int index = 0; index < nativeCount; index++)
            {
                host.Child("Button", $"native{index}", 0, 10);
            }

            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var provider = new AccessibleTestProvider(44, 0x10, "virtual", "")
                {
                    ActionName = "Check"
                };
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        Assert.Equal(1, await UiaControl.ReadCheckAsync(fixture.Root, "virtual", 1, default));
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(expected), await session.InvokeAsync("CHKBTN",
            [id, Value.String("virtual")], default));
    }
}
