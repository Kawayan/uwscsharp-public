using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class MmvAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RecordsActualGuestDisplayConditions()
    {
        AssertGuest();
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { },
                dpiAwarenessContext: -4);
        var monitors = new List<int[]>();
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.EnumDisplayMonitors(0, 0,
                (nint monitor, nint dc, ref Native.Rect rect,
                    nint data) =>
                {
                    monitors.Add([rect.Left, rect.Top,
                        rect.Right, rect.Bottom]);
                    return true;
                }, 0));
            Assert.NotEmpty(monitors);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "environment-inventory",
                user = Environment.UserName,
                dpi = GetDpiForWindow(fixture.Root),
                monitors,
                virtualOrigin = new[]
                {
                    Native.GetSystemMetrics(76),
                    Native.GetSystemMetrics(77)
                },
                fullMatrixAccepted = false
            }));
        }
    }

    [Theory]
    [InlineData(-1, 0, -4)]
    [InlineData(-1, 1, -4)]
    [InlineData(-1, 2, -4)]
    [InlineData(-2, 0, -4)]
    [InlineData(-2, 1, -4)]
    [InlineData(-2, 2, -4)]
    [InlineData(-4, 0, -4)]
    [InlineData(-4, 1, -4)]
    [InlineData(-4, 2, -4)]
    [InlineData(-1, 0, -1)]
    [InlineData(-1, 1, -1)]
    [InlineData(-1, 2, -1)]
    [InlineData(-2, 0, -1)]
    [InlineData(-2, 1, -1)]
    [InlineData(-2, 2, -1)]
    [InlineData(-4, 0, -1)]
    [InlineData(-4, 1, -1)]
    [InlineData(-4, 2, -1)]
    public async Task DirectMovementKeepsRootWithNestedChildren(
        int senderDpi, int hierarchy, int receiverDpi)
    {
        AssertGuest();
        var rootMessages = new ConcurrentQueue<long>();
        var childMessages = new ConcurrentQueue<uint>();
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                owner.InputMessage = (message, _, position) =>
                {
                    if (message == 0x200)
                    {
                        rootMessages.Enqueue((long)position);
                    }
                };
                if (hierarchy == 0)
                {
                    return;
                }

                nint child = owner.Child("STATIC", "枠付き子",
                    0x800000, 10, height: 120, width: 200);
                owner.ObserveInput(child, (message, _, _) =>
                    childMessages.Enqueue(message));
                if (hierarchy == 2)
                {
                    nint nested = owner.Child("EDIT", "入れ子",
                        0x800000, 20);
                    Assert.NotEqual(0, SetParent(nested, child));
                    owner.ObserveInput(nested,
                        (message, _, _) =>
                            childMessages.Enqueue(message));
                }
            }, dpiAwarenessContext: receiverDpi);
        MoveAwayFromCursor(fixture);
        Native.Point expected = ExpectedDirectPosition(fixture);
        fixture.Invoke(() => { });
        rootMessages.Clear();
        childMessages.Clear();
        Native.Point before = ReadOwnedCursor(fixture);

        InvokeWithDpi(senderDpi, session =>
        {
            Value id = Invoke(session, BuiltinFunctionNames.HndToId,
                Value.Numeric((long)fixture.Root));
            Invoke(session, BuiltinFunctionNames.MouseOrg,
                id, Value.Numeric(2));
            Assert.Equal(Value.Empty,
                Invoke(session, BuiltinFunctionNames.Mmv,
                    Value.Numeric(43), Value.Numeric(57)));
        });
        fixture.Invoke(() => { });

        Assert.Equal((long)(ushort)expected.X
                | ((long)(ushort)expected.Y << 16),
            Assert.Single(rootMessages));
        Assert.Empty(childMessages);
        Native.Point after = ReadOwnedCursor(fixture);
        Assert.Equal(before, after);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "direct-root",
            senderDpi,
            hierarchy,
            receiverDpi,
            windowDpi = GetDpiForWindow(fixture.Root),
            expected = new[] { expected.X, expected.Y },
            x = 43,
            y = 57
        }));
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-1, 0)]
    [InlineData(-1, 1)]
    [InlineData(-2, -1)]
    [InlineData(-2, 0)]
    [InlineData(-2, 1)]
    [InlineData(-4, -1)]
    [InlineData(-4, 0)]
    [InlineData(-4, 1)]
    public async Task NormalMovementUsesPhysicalOrigin(
        int senderDpi, int origin)
    {
        AssertGuest();
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { },
                dpiAwarenessContext: -4);
        Native.Point saved = ReadOwnedCursor(fixture);
        var expected = new Native.Point(43, 57);
        using (var dpi = new PhysicalDpiScope())
        {
            if (origin == 0)
            {
                Assert.True(Native.GetWindowRect(fixture.Root,
                    out Native.Rect bounds));
                expected = new(expected.X + bounds.Left,
                    expected.Y + bounds.Top);
            }
            else if (origin == 1)
            {
                Assert.True(Native.ClientToScreen(fixture.Root,
                    ref expected));
            }
        }

        try
        {
            Native.Point duringCall = default;
            InvokeWithDpi(senderDpi, session =>
            {
                if (origin >= 0)
                {
                    Value id = Invoke(session,
                        BuiltinFunctionNames.HndToId,
                        Value.Numeric((long)fixture.Root));
                    Invoke(session, BuiltinFunctionNames.MouseOrg,
                        id, Value.Numeric(origin));
                }
                Assert.Equal(Value.Empty,
                    Invoke(session, BuiltinFunctionNames.Mmv,
                        Value.Numeric(43), Value.Numeric(57)));
                duringCall = ReadOwnedCursor(fixture);
            });
            Native.Point actual = ReadOwnedCursor(fixture);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "normal-origin",
                senderDpi,
                origin,
                dpi = GetDpiForWindow(fixture.Root),
                expected = new[] { expected.X, expected.Y },
                duringCall = new[] { duringCall.X, duringCall.Y },
                actual = new[] { actual.X, actual.Y }
            }));
            Assert.Equal(expected, duringCall);
            Assert.Equal(expected, actual);
        }
        finally
        {
            RestoreOwnedCursor(fixture, saved);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-4)]
    public async Task NativePhysicalApiControl(int senderDpi)
    {
        AssertGuest();
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { },
                dpiAwarenessContext: -4);
        Native.Point saved = ReadOwnedCursor(fixture);
        Native.Point immediate = default;
        try
        {
            nint previous = Native.SetThreadDpiAwarenessContext(senderDpi);
            Assert.NotEqual(0, previous);
            try
            {
                Assert.True(Native.SetPhysicalCursorPos(43, 57));
                Assert.True(Native.GetPhysicalCursorPos(out immediate));
            }
            finally
            {
                Assert.NotEqual(0,
                    Native.SetThreadDpiAwarenessContext(previous));
            }
            Assert.True(Native.GetPhysicalCursorPos(out Native.Point after));
            Native.Point unscopedOwned = ReadOwnedCursor(fixture);
            // 固定した所有スレッドを対照にして、座標の尺度を校正します。
            fixture.Invoke(() =>
                Assert.True(Native.SetPhysicalCursorPos(43, 57)));
            Native.Point owned = ReadOwnedCursor(fixture);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "native-api-control",
                senderDpi,
                immediate = new[] { immediate.X, immediate.Y },
                afterRestore = new[] { after.X, after.Y },
                unscopedOwned = new[]
                {
                    unscopedOwned.X, unscopedOwned.Y
                },
                ownedPhysical = new[] { owned.X, owned.Y }
            }));
            Assert.Equal(new Native.Point(43, 57), immediate);
            // 認識を変更した呼出し元の値を物理位置の期待値にしません。
            Assert.Equal(immediate, owned);
        }
        finally
        {
            RestoreOwnedCursor(fixture, saved);
        }
    }

    private static Native.Point ReadOwnedCursor(
        PumpedControlFixture fixture)
    {
        Native.Point point = default;
        fixture.Invoke(() =>
        {
            using var dpi = new PhysicalDpiScope();
            Assert.True(AreDpiAwarenessContextsEqual(-4,
                GetThreadDpiAwarenessContext()));
            Assert.True(Native.GetPhysicalCursorPos(out point));
        });
        return point;
    }

    private static void MoveAwayFromCursor(
        PumpedControlFixture fixture)
    {
        fixture.Invoke(() =>
        {
            using var dpi = new PhysicalDpiScope();
            Assert.True(Native.GetPhysicalCursorPos(out Native.Point cursor));
            int left = Native.GetSystemMetrics(76);
            int top = Native.GetSystemMetrics(77);
            int width = Native.GetSystemMetrics(78);
            int height = Native.GetSystemMetrics(79);
            Assert.True(Native.GetWindowRect(fixture.Root,
                out Native.Rect original));
            int x = cursor.X > left + width / 2
                ? left + 20 : left + width - (original.Right - original.Left) - 20;
            int y = cursor.Y > top + height / 2
                ? top + 20 : top + height - (original.Bottom - original.Top) - 20;
            Assert.True(Native.SetWindowPos(fixture.Root, 0,
                x, y, 0, 0, Native.SwpNoSize
                    | Native.SwpNoZOrder | Native.SwpNoActivate));
            Assert.True(Native.GetWindowRect(fixture.Root,
                out Native.Rect bounds));
            Assert.False(cursor.X >= bounds.Left && cursor.X < bounds.Right
                && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom);
        });
    }

    private static void RestoreOwnedCursor(
        PumpedControlFixture fixture, Native.Point saved)
    {
        fixture.Invoke(() =>
        {
            using var dpi = new PhysicalDpiScope();
            Assert.True(Native.SetPhysicalCursorPos(saved.X, saved.Y));
            Assert.True(Native.GetPhysicalCursorPos(out Native.Point restored));
            Assert.Equal(saved, restored);
        });
    }

    private static Native.Point ExpectedDirectPosition(
        PumpedControlFixture fixture)
    {
        Native.Point point = default;
        fixture.Invoke(() =>
        {
            using (var dpi = new PhysicalDpiScope())
            {
                Assert.True(Native.ClientToScreen(fixture.Root, ref point));
                point = new(point.X + 43, point.Y + 57);
            }
            // 受信ウィンドウ固有の尺度を、製品の送信処理とは別に求めます。
            Assert.True(PhysicalToLogicalPointForPerMonitorDPI(
                fixture.Root, ref point));
            Assert.True(Native.ScreenToClient(fixture.Root, ref point));
        });
        return point;
    }

    private static void AssertGuest() =>
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);

    private static Value Invoke(IWindowsAutomationSession session,
        string name, params Value[] arguments) =>
        session.InvokeAsync(name, arguments, default)
            .AsTask().GetAwaiter().GetResult();

    private static void InvokeWithDpi(int context,
        Action<IWindowsAutomationSession> operation)
    {
        // DPIの保存と復元を同じスレッドの同期区間に限定します。
        nint previous = Native.SetThreadDpiAwarenessContext(context);
        Assert.NotEqual(0, previous);
        IWindowsAutomationSession? session = null;
        try
        {
            session = new WindowsAutomation().CreateSession();
            operation(session);
            Assert.True(AreDpiAwarenessContextsEqual(
                context, GetThreadDpiAwarenessContext()));
        }
        finally
        {
            try
            {
                session?.DisposeAsync().AsTask()
                    .GetAwaiter().GetResult();
            }
            finally
            {
                Assert.NotEqual(0,
                    Native.SetThreadDpiAwarenessContext(previous));
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint parent);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(
        nint first, nint second);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PhysicalToLogicalPointForPerMonitorDPI(
        nint window, ref Native.Point point);
}
