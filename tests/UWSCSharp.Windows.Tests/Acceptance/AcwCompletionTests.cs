using System.Diagnostics;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class AcwCompletionTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("size")]
    [InlineData("x")]
    [InlineData("y")]
    public async Task OmittedChildCoordinatesRemainRelativeToParent(string mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint child = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            child = owner.Child("STATIC", "ACW-child", 0, 17);
        }, title: "ACW-child-" + Guid.NewGuid(), dpiAwarenessContext: -4);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)child)], default);
        nint previous = Native.SetThreadDpiAwarenessContext(-4);
        Assert.NotEqual(0, previous);
        try
        {
            Assert.True(Native.GetWindowRect(child, out Native.Rect before));
            var origin = new Native.Point(0, 0);
            Assert.True(Native.ClientToScreen(fixture.Root, ref origin));
            Value[] arguments = mode switch
            {
                "size" => [id, new(ValueKind.Omitted), new(ValueKind.Omitted),
                    Value.Numeric(180), Value.Numeric(60)],
                "x" => [id, Value.Numeric(40)],
                "y" => [id, new(ValueKind.Omitted), Value.Numeric(60)],
                _ => [id]
            };
            ValueTask<Value> move = session.InvokeAsync(BuiltinFunctionNames.Acw,
                arguments, default);
            Assert.True(move.IsCompletedSuccessfully);
            Assert.Equal(Value.Empty, await move);
            Assert.True(Native.GetWindowRect(child, out Native.Rect after));
            Assert.Equal(mode == "x" ? origin.X + 40 : before.Left, after.Left);
            Assert.Equal(mode == "y" ? origin.Y + 60 : before.Top, after.Top);
            Assert.Equal(mode == "size" ? 180 : before.Right - before.Left,
                after.Right - after.Left);
            Assert.Equal(mode == "size" ? 60 : before.Bottom - before.Top,
                after.Bottom - after.Top);
        }
        finally
        {
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
        }
    }

    [Fact]
    public void UnsetIdCannotReuseTheLastTarget()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WindowsFunctions.Validate(BuiltinFunctionNames.Acw,
                [Value.Empty, Value.Numeric(40), Value.Numeric(60)]));
    }

    [Fact]
    public async Task CancelledWaitDoesNotMoveOrReplaceTheLastTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint child = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            child = owner.Child("STATIC", "ACW-cancel", 0, 17);
        }, title: "ACW-cancel-" + Guid.NewGuid(), dpiAwarenessContext: -4);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value rootId = await Register(session, fixture.Root);
        Value childId = await Register(session, child);
        await session.InvokeAsync(BuiltinFunctionNames.Acw, [rootId], default);
        Native.Rect rootBefore = Bounds(fixture.Root);
        Native.Rect childBefore = Bounds(child);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.Acw,
                [childId, Value.Numeric(40), Value.Numeric(60),
                    Value.Numeric(0), Value.Numeric(0), Value.Numeric(5000)], cancellation.Token));
        Assert.Equal(rootBefore, Bounds(fixture.Root));
        Assert.Equal(childBefore, Bounds(child));
        await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [Value.Numeric(-1)], default);
        await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [Value.Numeric(0), Value.Numeric(80), Value.Numeric(90)], default);
        Assert.Equal(80, Bounds(fixture.Root).Left);
        Assert.Equal(90, Bounds(fixture.Root).Top);
    }

    [Fact]
    public async Task OwnedTopLevelWindowDoesNotUseItsOwnersClientCoordinates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint popup = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            popup = Native.CreateWindowExW(0, "STATIC", "ACW-owned-popup",
                0x80000000, 210, 150, 300, 200, owner.Root, 0, 0, 0);
            Assert.NotEqual(0, popup);
        }, title: "ACW-owner-" + Guid.NewGuid(), dpiAwarenessContext: -4);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await Register(session, popup);
        Native.Rect before = Bounds(popup);
        Assert.Equal(fixture.Root, Native.GetParent(popup));
        await session.InvokeAsync(BuiltinFunctionNames.Acw, [id], default);
        Assert.Equal(before, Bounds(popup));
    }

    [Fact]
    public async Task InitialZeroDestroyedTargetAndDisposedSessionHaveDefinedBehavior()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, title: "ACW-lifetime-" + Guid.NewGuid(), dpiAwarenessContext: -4);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Native.Rect before = Bounds(fixture.Root);
        Assert.Equal(Value.Empty, await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [Value.Numeric(0), Value.Numeric(40), Value.Numeric(60)], default));
        Assert.Equal(before, Bounds(fixture.Root));
        Value id = await Register(session, fixture.Root);
        await session.InvokeAsync(BuiltinFunctionNames.Acw, [id], default);
        await using IWindowsAutomationSession other = new WindowsAutomation().CreateSession();
        await other.InvokeAsync(BuiltinFunctionNames.Acw,
            [Value.Numeric(0), Value.Numeric(40), Value.Numeric(60)], default);
        Assert.Equal(before, Bounds(fixture.Root));
        await fixture.DisposeAsync();
        Assert.Equal(Value.Empty, await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [Value.Numeric(0), Value.Numeric(40), Value.Numeric(60)], default));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.Acw, [id], default));
    }

    [Fact]
    public async Task PositiveWaitPrecedesTheMove()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ => { }, title: "ACW-wait-" + Guid.NewGuid(), dpiAwarenessContext: -4);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await Register(session, fixture.Root);
        var watch = Stopwatch.StartNew();
        await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [id, Value.Numeric(80), Value.Numeric(90), Value.Numeric(0),
                Value.Numeric(0), Value.Numeric(80)], default);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(70));
        Assert.Equal(80, Bounds(fixture.Root).Left);
        Assert.Equal(90, Bounds(fixture.Root).Top);
    }

    private static ValueTask<Value> Register(IWindowsAutomationSession session, nint window) =>
        session.InvokeAsync(BuiltinFunctionNames.HndToId, [Value.Numeric((long)window)], default);

    private static Native.Rect Bounds(nint window)
    {
        nint previous = Native.SetThreadDpiAwarenessContext(-4);
        Assert.NotEqual(0, previous);
        try
        {
            Assert.True(Native.GetWindowRect(window, out Native.Rect rect));
            return rect;
        }
        finally
        {
            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
        }
    }
}
