using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AccessibleStringTests
{
    private static Value N(long value) => Value.Numeric(value);

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object provider);

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AccStringsApplyModeSpecificNumberingAndKeepItemSnapshot(int mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        AccessibleTestProvider[] children;
        string first;
        if (mode == 4)
        {
            children =
            [
                new(41, 0, "  静\u4383的...  ", ""),
                new(30, 0, "  &Link...  ", ""),
                new(42, 0x40, " readonly label ", "value is not name"),
                new(42, 0, "editable", "excluded"),
                new(41, 1, "", ""),
                new(24, 0, "header", "")
            ];
            first = "静 的...";
        }
        else
        {
            int role = mode == 3 ? 42 : 29;
            children =
            [
                new(role, 0, "name is not value", "  value... \r\n"),
                new(role, 1, "empty value", ""),
                new(role, 0x40, "readonly", "excluded"),
                new(role, 4, "focused", "focused value")
            ];
            first = "  value... \r\n";
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("Button", "snapshot", 0, 10);
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var root = new AccessibleTestProvider(10, 0, "", "", children);
                return LresultFromObject(ref iid, (nuint)parameter, root);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM", [id, N(1)], default));
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal(["snapshot"], snapshot);
        Assert.Equal(Value.String(first), await session.InvokeAsync("GETSTR",
            [id, N(1), N(mode)], default));
        Assert.Equal(Value.String(first), await session.InvokeAsync("GETSTR",
            [id, N(-1), N(mode)], default));
        if (mode == 4)
        {
            Assert.Equal(Value.String(first), await session.InvokeAsync("GETSTR",
                [id, N(0), N(mode)], default));
            Assert.Equal(Value.String("&Link..."), await session.InvokeAsync("GETSTR",
                [id, N(2), N(mode)], default));
            Assert.Equal(Value.String("readonly label"), await session.InvokeAsync("GETSTR",
                [id, N(3), N(mode)], default));
            Assert.Equal(Value.String(""), await session.InvokeAsync("GETSTR",
                [id, N(4), N(mode)], default));
            Assert.Equal(Value.String(""), await session.InvokeAsync("GETSTR",
                [id, N(-4), N(mode)], default));
            Assert.Equal(Value.String("header"), await session.InvokeAsync("GETSTR",
                [id, N(5), N(mode)], default));
        }
        else
        {
            Assert.Equal(Value.String("focused value"), await session.InvokeAsync("GETSTR",
                [id, N(0), N(mode)], default));
            Assert.Equal(Value.String(""), await session.InvokeAsync("GETSTR",
                [id, N(2), N(mode)], default));
            Assert.Equal(Value.String(""), await session.InvokeAsync("GETSTR",
                [id, N(-2), N(mode)], default));
            Assert.Equal(Value.String("focused value"), await session.InvokeAsync("GETSTR",
                [id, N(3), N(mode)], default));
        }

        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR",
            [id, N(999), N(mode)], default)).Kind);
        Assert.Same(snapshot, session.AllItems);
        string constant = mode == 3 ? "STR_ACC_EDIT" : mode == 4 ? "STR_ACC_STATIC" : "STR_ACC_CELL";
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"PRINT GETSTR(HNDTOID({(long)fixture.Root}), 1, {constant})", output.Add);
        Assert.Equal([first], output);
        fixture.Dispose();
        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR", [id, N(1), N(mode)], default)).Kind);
        Assert.Same(snapshot, session.AllItems);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task EmptySentinelIsComparedAfterModeSpecificFormatting(int mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int role = mode == 3 ? 42 : mode == 4 ? 41 : 29;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var root = new AccessibleTestProvider(role, 0, "", "\u0001__EMPTY__");
                return LresultFromObject(ref iid, (nuint)parameter, root);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        if (mode != 4)
        {
            Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR",
                [id, N(0), N(mode)], default)).Kind);
        }

        Value result = await session.InvokeAsync("GETSTR", [id, N(1), N(mode)], default);
        Assert.Equal(mode == 4 ? Value.String("__EMPTY__") : new Value(ValueKind.Empty), result);
    }

    [Fact]
    public async Task StaticStringCountsDuplicateGeometryUnlikeItemEnumeration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var root = new AccessibleTestProvider(10, 0, "", "",
                    new(41, 0, "duplicate", ""), new(41, 0, "duplicate", ""));
                return LresultFromObject(ref iid, (nuint)parameter, root);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM", [id, N(8388608)], default));
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal(Value.String("duplicate"), await session.InvokeAsync("GETSTR",
            [id, N(2), N(4)], default));
        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR", [id, N(3), N(4)], default)).Kind);
        Assert.Same(snapshot, session.AllItems);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task StringReadPropagatesCancellationAndComErrorThenRecovers(int mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int role = mode == 3 ? 42 : mode == 4 ? 41 : 29;
        Action? reading = null;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("Button", "snapshot", 0, 10);
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var root = new AccessibleTestProvider(role, 0, "name", "good")
                {
                    ReadingValue = Volatile.Read(ref reading)
                };
                return LresultFromObject(ref iid, (nuint)parameter, root);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, N(1)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        using var cancellation = new CancellationTokenSource();
        Volatile.Write(ref reading, cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETSTR", [id, N(1), N(mode)], cancellation.Token));
        Assert.Same(snapshot, session.AllItems);
        Volatile.Write(ref reading, () => throw new COMException("所有providerの失敗です。",
            unchecked((int)0x80004005)));
        COMException error = await Assert.ThrowsAsync<COMException>(async () =>
            await session.InvokeAsync("GETSTR", [id, N(1), N(mode)], default));
        Assert.Equal(unchecked((int)0x80004005), error.HResult);
        Assert.Same(snapshot, session.AllItems);
        Volatile.Write(ref reading, null);
        Assert.Equal(Value.String("good"), await session.InvokeAsync("GETSTR",
            [id, N(1), N(mode)], default));
        Assert.Same(snapshot, session.AllItems);
    }

    [Fact]
    public async Task NativeEditAndStaticProvidersUseAccStringPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("Edit", "  native... \r\n", 4, 10);
            host.Child("Edit", "readonly", 0x800, 45);
            host.Child("Edit", "zero width", 0, 60, width: 0);
            host.Child("Static", "  native static...  ", 0, 80);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(Value.String("  native... \r\n"), await session.InvokeAsync("GETSTR",
            [id, N(1), N(3)], default));
        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR", [id, N(2), N(3)], default)).Kind);
        Assert.Equal(Value.String("UIA取得専用window"), await session.InvokeAsync("GETSTR",
            [id, N(0), N(4)], default));
        Assert.Equal(Value.String("readonly"), await session.InvokeAsync("GETSTR",
            [id, N(2), N(4)], default));
        Assert.Equal(Value.String("native static..."), await session.InvokeAsync("GETSTR",
            [id, N(3), N(4)], default));
    }
}
