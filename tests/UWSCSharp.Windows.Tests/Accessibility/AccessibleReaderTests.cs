using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AccessibleReaderTests(ITestOutputHelper diagnostics)
{
    private static Value N(int value) => Value.Numeric(value);

    /// <summary>
    /// 座標探索へ単純子IDを返す検証用プロバイダーです。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class PointProvider(AccessibleTestProvider child)
        : AccessibleTestProvider(10, 0, "親", "", child), IAccessibleNative
    {
        object IAccessibleNative.HitTest(int x, int y) => 1;
    }

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object accessible);

    [Fact]
    public async Task PosAccScreenRouteKeepsTheSimpleChildIdAndProviderFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var child = new AccessibleTestProvider(42, 0, "子", "子の値");
        var provider = new PointProvider(child) { SimpleChildren = true };
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
            Assert.True(Native.SetWindowPos(host.Root, -1,
                0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate));
        }, initializeOle: true);
        using var dpi = new PhysicalDpiScope();
        var point = new Native.Point(15, 15);
        Assert.True(Native.ClientToScreen(fixture.Root, ref point));
        nint found = Native.WindowFromPoint(point);
        Native.GetWindowThreadProcessId(found, out uint owner);
        Assert.Equal((uint)Environment.ProcessId, owner);
        Assert.Equal(fixture.Root, found);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")).CreateSession();
        Assert.Equal(Value.String("子の値"), await session.InvokeAsync(
            BuiltinFunctionNames.PosAcc,
            [N(0), N(point.X), N(point.Y), N(4 | 512)], default));
        child.ReadingValue = () => throw new COMException(
            "プロバイダーの取得失敗", unchecked((int)0x80004005));
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.PosAcc,
                [N(0), N(point.X), N(point.Y), N(4 | 512)], default));
        Assert.Equal(unchecked((int)0x80004005), failure.HResult);
        Assert.True(WindowsOperationErrors.Contains(failure));
        child.ReadingValue = null;
        Assert.Equal(Value.String("子の値"), await session.InvokeAsync(
            BuiltinFunctionNames.PosAcc,
            [N(0), N(point.X), N(point.Y), N(4 | 512)], default));
    }

    [Fact]
    public async Task PosAccScreenRouteReadsOnlyTheOwnedPointAndRestoresDpi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dpi = new PhysicalDpiScope();
        using var fixture = new ButtonControlTests.Fixture();
        nint check = fixture.Child("BUTTON", "画面座標", 3, 10);
        Assert.True(Native.SetWindowPos(fixture.Root, -1,
            0, 0, 0, 0, Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate));
        var point = new Native.Point(15, 15);
        Assert.True(Native.ClientToScreen(fixture.Root, ref point));
        Assert.Equal(check, Native.WindowFromPoint(point));
        Native.GetWindowThreadProcessId(check, out uint owner);
        Assert.Equal((uint)Environment.ProcessId, owner);
        await using IWindowsAutomationSession session = fixture.Session();
        nint previous = Native.SetThreadDpiAwarenessContext(-1);
        try
        {
            Assert.NotEqual(0, previous);
            nint expected = Native.SetThreadDpiAwarenessContext(-1);
            Assert.Equal(Value.String("画面座標"), await session.InvokeAsync(
                BuiltinFunctionNames.PosAcc,
                [N(0), N(point.X), N(point.Y), N(3 | 512)], default));
            Assert.Equal(expected, Native.SetThreadDpiAwarenessContext(-1));
            Assert.Equal(Value.String(""), await session.InvokeAsync(
                BuiltinFunctionNames.PosAcc,
                [N(0), N(int.MinValue), N(int.MinValue), N(3)], default));
            Assert.Equal(Value.String(""), await session.InvokeAsync(
                BuiltinFunctionNames.PosAcc,
                [N(0), N(int.MaxValue), N(int.MaxValue), N(3)], default));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync(BuiltinFunctionNames.PosAcc,
                    [N(0), N(point.X), N(point.Y), N(3)], cancellation.Token));
        }
        finally
        {
            Native.SetThreadDpiAwarenessContext(previous);
        }
    }

    [Fact]
    public async Task PosAccReadsDedicatedControlPropertiesAndClientCoordinates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture(physicalCoordinates: true);
        nint check = fixture.Child("BUTTON", "&検証", 3, 10);
        nint edit = fixture.Child("EDIT", "本文", 0, 60);
        using (var physical = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(edit, out Native.Rect bounds));
            var point = new Native.Point(bounds.Left, bounds.Top);
            Assert.True(Native.ScreenToClient(fixture.Root, ref point));
            diagnostics.WriteLine($"EDIT physical client: {point.X},{point.Y},{bounds.Right - bounds.Left},{bounds.Bottom - bounds.Top}");
        }
        Assert.NotEqual(0, Native.SendMessageTimeoutW(check, 0xf1,
            1, 0, 2, 1000, out _));
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String("検証"), await session.InvokeAsync(
            "POSACC", [id, N(15), N(15), N(3 | 512)], default));
        Assert.Equal(Value.String("検証"), await session.InvokeAsync(
            "POSACC", [id, N(15), N(15)], default));
        Assert.Equal(Value.String(AccessibleReader.RoleText(44)), await session.InvokeAsync(
            "POSACC", [id, N(15), N(15), N(5)], default));
        Value state = await session.InvokeAsync("POSACC", [id, N(15), N(15), N(6)], default);
        Assert.Contains(AccessibleReader.StateText(16), state.Display());
        Assert.Equal(Value.String("本文"), await session.InvokeAsync(
            "POSACC", [id, N(15), N(65), N(4)], default));
        Assert.Equal(Value.String(""), await session.InvokeAsync(
            "POSACC", [id, N(15), N(15), N(7)], default));
        Native.Rect rect;
        using (var physical = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(check, out rect));
        }
        Assert.Equal(Value.String($"10,10,{rect.Right - rect.Left},{rect.Bottom - rect.Top}"),
            await session.InvokeAsync("POSACC", [id, N(15), N(15), N(8)], default));
        Assert.Equal(Value.String(""), await session.InvokeAsync(
            "POSACC", [id, N(-1), N(15)], default));
        Assert.Equal(Value.String(""), await session.InvokeAsync(
            "POSACC", [id, N(int.MaxValue), N(15)], default));
        Assert.Equal(Value.String(""), await session.InvokeAsync(
            "POSACC", [N(999), N(15), N(15)], default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("POSACC", [id, N(15), N(15)], cancellation.Token));
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT POSACC(id, 15, 15, ACC_NAME OR ACC_BACK)
                PRINT POSACC(id, 15, 65, ACC_VALUE)
                """, output.Add);
        Assert.Equal(["検証", "本文"], output);
        Assert.True(Native.DestroyWindow(fixture.Root));
        Assert.Equal(Value.String(""), await session.InvokeAsync(
            "POSACC", [id, N(15), N(15)], default));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync("POSACC", [id, N(15), N(15)], default));
    }
}
