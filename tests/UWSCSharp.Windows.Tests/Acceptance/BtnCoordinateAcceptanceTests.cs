using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class BtnCoordinateAcceptanceTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> PhysicalOrigins()
    {
        foreach (int senderDpi in new[] { -1, -2, -4 })
        {
            foreach (int origin in new[] { -1, 0, 1 })
            {
                foreach (bool blocked in new[] { false, true })
                {
                    yield return [senderDpi, origin, blocked];
                }
            }
        }
    }

    public static IEnumerable<object[]> DirectHierarchies()
    {
        foreach (int senderDpi in new[] { -1, -2, -4 })
        {
            foreach (int receiverDpi in new[] { -1, -4 })
            {
                foreach (int hierarchy in new[] { 0, 1, 2, 3 })
                {
                    yield return [senderDpi, receiverDpi, hierarchy];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(PhysicalOrigins))]
    public async Task ButtonMovesToPhysicalOriginBeforeSendingInput(
        int senderDpi, int origin, bool blocked)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { },
                dpiAwarenessContext: -4);
        Native.Point saved = ReadCursor(fixture);
        var expected = new Native.Point(43, 57);
        fixture.Invoke(() =>
        {
            using var dpi = new PhysicalDpiScope();
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
        });
        var inputs = new List<(uint Flags, Native.Point Position)>();
        try
        {
            // 毎回異なる位置から開始し、移動しない場合の偽陽性を防ぎます。
            var sentinel = new Native.Point(expected.X + 160,
                expected.Y + 120);
            SetCursor(fixture, sentinel);
            Assert.NotEqual(expected, ReadCursor(fixture));
            InvokeWithDpi(senderDpi, fixture, inputs, session =>
            {
                Invoke(session, BuiltinFunctionNames.LockHard,
                    Value.Numeric(blocked ? 1 : 0));
                if (origin >= 0)
                {
                    Value id = Invoke(session,
                        BuiltinFunctionNames.HndToId,
                        Value.Numeric((long)fixture.Root));
                    Invoke(session, BuiltinFunctionNames.MouseOrg,
                        id, Value.Numeric(origin));
                }
                Assert.Equal(Value.Empty,
                    Invoke(session, BuiltinFunctionNames.Btn,
                        Value.Numeric(0), Value.Numeric(0),
                        Value.Numeric(43), Value.Numeric(57)));
            });
            Assert.Collection(inputs,
                down =>
                {
                    Assert.Equal(2u, down.Flags);
                    Assert.Equal(expected, down.Position);
                },
                up =>
                {
                    Assert.Equal(4u, up.Flags);
                    Assert.Equal(expected, up.Position);
                });
            Assert.Equal(expected, ReadCursor(fixture));
            output.WriteLine(JsonSerializer.Serialize(new
            {
                senderDpi,
                origin,
                blocked,
                dpi = GetDpiForWindow(fixture.Root),
                expected = new[] { expected.X, expected.Y },
                duringDown = new[] { inputs[0].Position.X, inputs[0].Position.Y },
                duringUp = new[] { inputs[1].Position.X, inputs[1].Position.Y },
                buttonNotifications = "recording-backend",
                physicalCursor = "native"
            }));
        }
        finally
        {
            SetCursor(fixture, saved);
        }
    }

    [Theory]
    [MemberData(nameof(DirectHierarchies))]
    public async Task DirectButtonSelectsOwnedLeafWithoutMovingCursor(
        int senderDpi, int receiverDpi, int hierarchy)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var messages = new ConcurrentQueue<(nint Window, uint Message,
            long Parameter, Native.Point Screen)>();
        Exception? callbackFailure = null;
        nint target = 0;
        nint receiverRoot = 0;
        void Observe(nint window, uint message, nint parameter)
        {
            if (message is not (0x201 or 0x202))
            {
                return;
            }
            try
            {
                // 受信側の論理client座標を物理画面へ換算した値も診断に残します。
                var screen = new Native.Point(unchecked((short)(long)parameter),
                    unchecked((short)((long)parameter >> 16)));
                Assert.True(Native.ClientToScreen(window, ref screen));
                Assert.True(LogicalToPhysicalPointForPerMonitorDPI(receiverRoot, ref screen));
                messages.Enqueue((window, message, (long)parameter, screen));
            }
            catch (Exception error)
            {
                callbackFailure = error;
            }
        }
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                target = owner.Root;
                receiverRoot = owner.Root;
                owner.InputMessage = (message, _, parameter) =>
                    Observe(owner.Root, message, parameter);
                if (hierarchy == 0)
                {
                    return;
                }
                nint child = owner.Child(hierarchy == 3 ? "EDIT" : "STATIC", "枠付き子",
                    0x800000, 10, height: 140, width: 240);
                owner.ObserveInput(child, (message, _, parameter) =>
                    Observe(child, message, parameter));
                target = child;
                if (hierarchy == 3)
                {
                    nint front = owner.Child("EDIT", "前側",
                        0x800000, 10, height: 140, width: 240);
                    Assert.True(Native.SetWindowPos(front, 0, 0, 0, 0, 0,
                        Native.SwpNoSize | Native.SwpNoMove | Native.SwpNoActivate));
                    Assert.Equal(front, Native.GetWindow(owner.Root, 5));
                    owner.ObserveInput(front, (message, _, parameter) =>
                        Observe(front, message, parameter));
                    target = front;
                }
                if (hierarchy == 2)
                {
                    nint nested = owner.Child("EDIT", "入れ子",
                        0x800000, 20, height: 60, width: 140);
                    Assert.NotEqual(0, SetParent(nested, child));
                    owner.ObserveInput(nested, (message, _, parameter) =>
                        Observe(nested, message, parameter));
                    target = nested;
                }
            }, dpiAwarenessContext: receiverDpi);
        MoveAwayFromCursor(fixture);
        var expected = new Native.Point(23, 27);
        Native.Point requested = default;
        Native.Point intendedScreen = default;
        Native.Point messagePhysicalScreen = default;
        Native.Point expectedReceived = expected;
        Native.Point childTransform = default;
        Native.Point physicalClientOrigin = default;
        Native.Point logicalClientOrigin = default;
        Native.Rect physicalWindowBounds = default;
        Native.Rect logicalWindowBounds = default;
        fixture.Invoke(() =>
        {
            using (var dpi = new PhysicalDpiScope())
            {
                var rootOrigin = new Native.Point(0, 0);
                Assert.True(Native.ClientToScreen(fixture.Root, ref rootOrigin));
                if (hierarchy == 0)
                {
                    intendedScreen = new(rootOrigin.X + expected.X,
                        rootOrigin.Y + expected.Y);
                }
                else
                {
                    if (Native.DwmGetWindowAttribute(target, 9,
                        out Native.Rect bounds, Marshal.SizeOf<Native.Rect>()) != 0)
                    {
                        Assert.True(Native.GetWindowRect(target, out bounds));
                    }
                    intendedScreen = new(bounds.Left + expected.X,
                        bounds.Top + expected.Y);
                }
                requested = new(intendedScreen.X - rootOrigin.X,
                    intendedScreen.Y - rootOrigin.Y);
                Assert.True(Native.ClientToScreen(target, ref expectedReceived));
                messagePhysicalScreen = expectedReceived;
                Assert.True(Native.ClientToScreen(target, ref physicalClientOrigin));
                Assert.True(Native.GetWindowRect(target, out physicalWindowBounds));
            }
            Assert.True(Native.ClientToScreen(target, ref logicalClientOrigin));
            Assert.True(Native.GetWindowRect(target, out logicalWindowBounds));
            // 子自身の変換との差も残し、トップレベルの尺度を対照で確認します。
            childTransform = expectedReceived;
            Assert.True(PhysicalToLogicalPointForPerMonitorDPI(
                target, ref childTransform));
            Assert.True(Native.ScreenToClient(target, ref childTransform));
            Assert.True(PhysicalToLogicalPointForPerMonitorDPI(
                receiverRoot, ref expectedReceived));
            Assert.True(Native.ScreenToClient(target, ref expectedReceived));
        });
        uint packed = unchecked((uint)(ushort)expected.X
            | ((uint)(ushort)expected.Y << 16));
        long receivedPacked = (long)(ushort)expectedReceived.X
            | ((long)(ushort)expectedReceived.Y << 16);
        var requestedClientPhysical = new Native.Point(
            physicalClientOrigin.X + expected.X,
            physicalClientOrigin.Y + expected.Y);
        (Native.Point Logical, Native.Point Physical)[] nearest =
            NearestRepresentablePoints(fixture, target,
            receiverRoot, physicalClientOrigin,
            requestedClientPhysical, expectedReceived);
        messages.Clear();
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.PostMessageW(target, 0x201, 0,
                unchecked((nint)(nuint)packed)));
            Assert.True(Native.PostMessageW(target, 0x202, 0,
                unchecked((nint)(nuint)packed)));
        }
        fixture.Invoke(() => { });
        Assert.Null(callbackFailure);
        (nint Window, uint Message, long Parameter, Native.Point Screen)[]
            physicalControl = [.. messages];
        messages.Clear();
        nint previousContext = Native.SetThreadDpiAwarenessContext(
            GetWindowDpiAwarenessContext(target));
        Assert.NotEqual(0, previousContext);
        try
        {
            Assert.True(Native.PostMessageW(target, 0x201, 0,
                unchecked((nint)(nuint)receivedPacked)));
            Assert.True(Native.PostMessageW(target, 0x202, 0,
                unchecked((nint)(nuint)receivedPacked)));
        }
        finally
        {
            Assert.NotEqual(0,
                Native.SetThreadDpiAwarenessContext(previousContext));
        }
        fixture.Invoke(() => { });
        Assert.Null(callbackFailure);
        (nint Window, uint Message, long Parameter, Native.Point Screen)[]
            explicitControl = [.. messages];
        output.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "native-calibration",
            senderDpi,
            receiverDpi,
            hierarchy,
            messagePhysicalScreen = new[] { messagePhysicalScreen.X, messagePhysicalScreen.Y },
            expectedFromRoot = new[] { expectedReceived.X, expectedReceived.Y },
            expectedFromChild = new[] { childTransform.X, childTransform.Y },
            physicalClientOrigin = new[] { physicalClientOrigin.X, physicalClientOrigin.Y },
            logicalClientOrigin = new[] { logicalClientOrigin.X, logicalClientOrigin.Y },
            physicalWindowOrigin = new[] { physicalWindowBounds.Left, physicalWindowBounds.Top },
            logicalWindowOrigin = new[] { logicalWindowBounds.Left, logicalWindowBounds.Top },
            requestedClientPhysical = new[] { requestedClientPhysical.X, requestedClientPhysical.Y },
            nearest = nearest.Select(point => new
            {
                logical = new[] { point.Logical.X, point.Logical.Y },
                physical = new[] { point.Physical.X, point.Physical.Y }
            }),
            actual = physicalControl.Select(message => new[]
            {
                unchecked((short)message.Parameter),
                unchecked((short)(message.Parameter >> 16))
            }),
            physical = physicalControl.Select(message => new[] { message.Screen.X, message.Screen.Y }),
            explicitActual = explicitControl.Select(message => new[]
            {
                unchecked((short)message.Parameter),
                unchecked((short)(message.Parameter >> 16))
            }),
            explicitPhysical = explicitControl.Select(message => new[] { message.Screen.X, message.Screen.Y })
        }));
        Assert.Equal([receivedPacked, receivedPacked],
            explicitControl.Select(message => message.Parameter));
        Assert.All(explicitControl, message =>
            AssertNearest(message.Parameter, message.Screen, nearest));
        Assert.Equal([0x201u, 0x202u],
            physicalControl.Select(message => message.Message));
        Assert.All(physicalControl, message =>
        {
            Assert.Equal(target, message.Window);
            AssertNearest(message.Parameter, message.Screen, nearest);
        });
        Native.Point[] nativeControl = [.. physicalControl.Select(message => message.Screen)];
        messages.Clear();
        Native.Point before = ReadCursor(fixture);
        var inputs = new List<(uint Flags, Native.Point Position)>();
        InvokeWithDpi(senderDpi, fixture, inputs, session =>
        {
            Invoke(session, BuiltinFunctionNames.LockHard, Value.Numeric(1));
            Value id = Invoke(session, BuiltinFunctionNames.HndToId,
                Value.Numeric((long)fixture.Root));
            Invoke(session, BuiltinFunctionNames.MouseOrg,
                id, Value.Numeric(2));
            Assert.Equal(Value.Empty,
                Invoke(session, BuiltinFunctionNames.Btn,
                    Value.Numeric(0), Value.Numeric(0),
                    Value.Numeric(requested.X), Value.Numeric(requested.Y)));
        });
        fixture.Invoke(() => { });
        Assert.Null(callbackFailure);
        Assert.Collection(messages,
            down =>
            {
                Assert.Equal(target, down.Window);
                Assert.Equal(0x201u, down.Message);
                Assert.Equal(physicalControl[0].Parameter, down.Parameter);
                AssertNearest(down.Parameter, down.Screen, nearest);
            },
            up =>
            {
                Assert.Equal(target, up.Window);
                Assert.Equal(0x202u, up.Message);
                Assert.Equal(physicalControl[1].Parameter, up.Parameter);
                AssertNearest(up.Parameter, up.Screen, nearest);
            });
        Assert.Empty(inputs);
        Assert.Equal(before, ReadCursor(fixture));
        Assert.Null(callbackFailure);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            senderDpi,
            receiverDpi,
            hierarchy,
            rootDpi = GetDpiForWindow(fixture.Root),
            targetDpi = GetDpiForWindow(target),
            requested = new[] { requested.X, requested.Y },
            expected = new[] { expected.X, expected.Y },
            expectedReceived = new[] { expectedReceived.X, expectedReceived.Y },
            intendedScreen = new[] { intendedScreen.X, intendedScreen.Y },
            messagePhysicalScreen = new[] { messagePhysicalScreen.X, messagePhysicalScreen.Y },
            nativeControlPhysicalScreen = nativeControl.Select(point =>
                new[] { point.X, point.Y }),
            receiverPhysicalScreen = messages.Select(message =>
                new[] { message.Screen.X, message.Screen.Y }),
            expectedBasis = hierarchy == 0
                ? "root-client-physical" : "selected-outer-physical",
            receiverRenderingAccepted = false
        }));
    }

    private static void AssertNearest(long parameter, Native.Point physical,
        IReadOnlyList<(Native.Point Logical, Native.Point Physical)> nearest)
    {
        var logical = new Native.Point(unchecked((short)parameter),
            unchecked((short)(parameter >> 16)));
        Assert.Contains(nearest, point =>
            point.Logical.Equals(logical) && point.Physical.Equals(physical));
    }

    private static (Native.Point Logical, Native.Point Physical)[]
        NearestRepresentablePoints(PumpedControlFixture fixture,
            nint target, nint root, Native.Point physicalOrigin,
            Native.Point desired, Native.Point seed)
    {
        var result = new List<(Native.Point Logical, Native.Point Physical)>();
        fixture.Invoke(() =>
        {
            Assert.True(Native.GetClientRect(target, out Native.Rect bounds));
            Assert.InRange(bounds.Right, 1, 2048);
            Assert.InRange(bounds.Bottom, 1, 2048);
            Assert.InRange(seed.X, 0, bounds.Right - 1);
            Assert.InRange(seed.Y, 0, bounds.Bottom - 1);
            Native.Point Project(int x, int y)
            {
                var point = new Native.Point(x, y);
                Assert.True(Native.ClientToScreen(target, ref point));
                Assert.True(LogicalToPhysicalPointForPerMonitorDPI(root, ref point));
                return point;
            }
            Assert.Equal(physicalOrigin, Project(0, 0));
            var nearestX = new List<int>();
            var nearestY = new List<int>();
            long distanceX = long.MaxValue;
            long distanceY = long.MaxValue;
            for (int x = 0; x < bounds.Right; x++)
            {
                long distance = Math.Abs((long)Project(x, seed.Y).X - desired.X);
                if (distance < distanceX)
                {
                    distanceX = distance;
                    nearestX.Clear();
                }
                if (distance == distanceX)
                {
                    nearestX.Add(x);
                }
            }
            for (int y = 0; y < bounds.Bottom; y++)
            {
                long distance = Math.Abs((long)Project(seed.X, y).Y - desired.Y);
                if (distance < distanceY)
                {
                    distanceY = distance;
                    nearestY.Clear();
                }
                if (distance == distanceY)
                {
                    nearestY.Add(y);
                }
            }
            foreach (int x in nearestX)
            {
                foreach (int y in nearestY)
                {
                    Native.Point physical = Project(x, y);
                    Assert.Equal(distanceX, Math.Abs((long)physical.X - desired.X));
                    Assert.Equal(distanceY, Math.Abs((long)physical.Y - desired.Y));
                    result.Add((new(x, y), physical));
                }
            }
        });
        return [.. result];
    }

    private static void MoveAwayFromCursor(PumpedControlFixture fixture)
    {
        fixture.Invoke(() =>
        {
            using var dpi = new PhysicalDpiScope();
            Assert.True(Native.GetPhysicalCursorPos(out Native.Point cursor));
            Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect original));
            int left = Native.GetSystemMetrics(76);
            int top = Native.GetSystemMetrics(77);
            int width = Native.GetSystemMetrics(78);
            int height = Native.GetSystemMetrics(79);
            int x = cursor.X > left + width / 2
                ? left + 20 : left + width - (original.Right - original.Left) - 20;
            int y = cursor.Y > top + height / 2
                ? top + 20 : top + height - (original.Bottom - original.Top) - 20;
            Assert.True(Native.SetWindowPos(fixture.Root, 0, x, y, 0, 0,
                Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoActivate));
            Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect bounds));
            Assert.False(cursor.X >= bounds.Left && cursor.X < bounds.Right
                && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom);
        });
    }

    private static Value Invoke(IWindowsAutomationSession session,
        string name, params Value[] arguments) =>
        session.InvokeAsync(name, arguments, default)
            .AsTask().GetAwaiter().GetResult();

    private static void InvokeWithDpi(int senderDpi,
        PumpedControlFixture fixture,
        List<(uint Flags, Native.Point Position)> inputs,
        Action<IWindowsAutomationSession> operation)
    {
        // DPIの設定と復元はawaitを挟まず、同じスレッドで行います。
        nint previous = Native.SetThreadDpiAwarenessContext(senderDpi);
        Assert.NotEqual(0, previous);
        IWindowsAutomationSession? session = null;
        try
        {
            // ボタン通知だけを記録し、OSのカーソル移動は実経路を通します。
            void Capture(Native.Input input)
            {
                Assert.Equal(0u, input.Type);
                inputs.Add((input.Data.Mouse.Flags, ReadCursor(fixture)));
            }
            session = new WindowsAutomation(Capture,
                swappedMouseButtons: false,
                inputOwnershipFactory: () => new NativeInputOwnershipBackend(
                    send: input =>
                    {
                        Capture(input);
                        return (1u, 0);
                    })).CreateSession();
            operation(session);
            Assert.True(AreDpiAwarenessContextsEqual(senderDpi,
                GetThreadDpiAwarenessContext()));
        }
        finally
        {
            try
            {
                session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                Assert.NotEqual(0,
                    Native.SetThreadDpiAwarenessContext(previous));
            }
        }
    }

    private static Native.Point ReadCursor(PumpedControlFixture fixture)
    {
        Native.Point point = default;
        fixture.Invoke(() =>
        {
            using var dpi = new PhysicalDpiScope();
            Assert.True(Native.GetPhysicalCursorPos(out point));
        });
        return point;
    }

    private static void SetCursor(PumpedControlFixture fixture,
        Native.Point point)
    {
        fixture.Invoke(() =>
        {
            using var dpi = new PhysicalDpiScope();
            Assert.True(Native.SetPhysicalCursorPos(point.X, point.Y));
            Assert.True(Native.GetPhysicalCursorPos(out Native.Point actual));
            Assert.Equal(point, actual);
        });
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint parent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogicalToPhysicalPointForPerMonitorDPI(
        nint window, ref Native.Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PhysicalToLogicalPointForPerMonitorDPI(
        nint window, ref Native.Point point);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern nint GetWindowDpiAwarenessContext(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first,
        nint second);
}
