using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class WindowMovementRecordingTests
{
    private static readonly RecordedWindow Initial =
        new(1, "ChatGPT", "Chrome_WidgetWin_1", 986, 666, 2806, 1478);

    [Fact]
    public void NativeMovementHooksCanBeRegisteredAndReleased()
    {
        using var monitor = new WindowMovementMonitor(new RecordingBuffer([], 0),
            _ => true, error => throw error);
        monitor.Start();
        monitor.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowDragProducesAcwDuringHoldAndAfterRelease(bool relative)
    {
        var buffer = new RecordingBuffer([], 0)
        {
            OmitIdleTimeAndMouseMoves = true
        };
        RecordedWindow current = Initial;
        using var monitor = new WindowMovementMonitor(buffer, _ => false,
            error => throw error, () => 1, _ => current);
        buffer.Mouse(0, 1, 2291, 699, 1, false, Initial);
        buffer.Mouse(-1, 1, 2301, 696, 2, false, Initial);
        monitor.ProcessEvent(0x000a, 1, 0, 0, 3);
        current = Initial with
        {
            X = 996,
            Y = 663
        };
        monitor.ProcessEvent(0x800b, 1, 0, 0, 4);
        buffer.Mouse(-1, 1, 2301, 696, 5, false, current);
        current = Initial with
        {
            X = 1164,
            Y = 800
        };
        monitor.ProcessEvent(0x800b, 1, 0, 0, 6);
        buffer.Mouse(0, 2, 2470, 834, 7, false, current);
        monitor.ProcessEvent(0x000b, 1, 0, 0, 8);
        current = current with
        {
            Y = 801
        };
        monitor.ProcessEvent(0x800b, 1, 0, 0, 9);
        string source = BasicRecorder.Serialize(buffer.Events, true, relative);
        Assert.Equal(string.Join(Environment.NewLine,
            "ACW(GETID(\"ChatGPT\",\"Chrome_WidgetWin_1\"),986,666,2806,1478,0)",
            "BTN(0,1,2291,699)",
            "ACW(0,996,663,0,0,0)",
            "ACW(0,1164,800,0,0,0)",
            "BTN(0,2,2470,834)",
            "ACW(0,1164,801,0,0,0)", ""), source);
    }

    [Fact]
    public void MoveNotificationsIgnoreChildrenExcludedWindowsAndDuplicates()
    {
        var buffer = new RecordingBuffer([], 0);
        RecordedWindow current = Initial;
        using var monitor = new WindowMovementMonitor(buffer, window => window == 2,
            error => throw error, () => 1, _ => current);
        monitor.ProcessEvent(0x000a, 2, 0, 0, 1);
        monitor.ProcessEvent(0x000a, 3, 0, 0, 1);
        monitor.ProcessEvent(0x800b, 1, 0, 0, 1);
        Assert.Empty(buffer.Events);
        monitor.ProcessEvent(0x000a, 1, 0, 0, 2);
        monitor.ProcessEvent(0x800b, 1, 0, 0, 3);
        current = current with
        {
            Width = 3000
        };
        monitor.ProcessEvent(0x800b, 1, -4, 0, 4);
        monitor.ProcessEvent(0x800b, 1, 0, 1, 4);
        monitor.ProcessEvent(0x800b, 1, 0, 0, 5);
        string source = BasicRecorder.Serialize(buffer.Events, true);
        Assert.Equal(2, source.Split("ACW(").Length - 1);
        Assert.Contains("ACW(0,986,666,3000,0,0)", source);
    }

    [Fact]
    public void MovementAtCapacityDoesNotRewriteSavedPrefix()
    {
        var buffer = new RecordingBuffer([], 0, 1);
        buffer.Mouse(0, 1, 10, 20, 1, false, Initial);
        BasicRecorder.InputEvent saved = Assert.Single(buffer.Events);
        buffer.WindowMovement(Initial, 1, 2);
        Assert.True(buffer.Overflowed);
        Assert.Equal(saved, Assert.Single(buffer.Events));
    }

    [Fact]
    public void LostEndSnapshotStillEndsMovementAndPreservesLaterClientDrag()
    {
        var buffer = new RecordingBuffer([], 0)
        {
            OmitIdleTimeAndMouseMoves = true
        };
        RecordedWindow? current = Initial;
        using var monitor = new WindowMovementMonitor(buffer, _ => false,
            error => throw error, () => 1, _ => current);
        monitor.ProcessEvent(0x000a, 1, 0, 0, 1);
        current = null;
        monitor.ProcessEvent(0x000b, 1, 0, 0, 2);
        buffer.Mouse(0, 1, 10, 20, 3, false, Initial);
        buffer.Mouse(-1, 1, 20, 30, 4, false, Initial);
        buffer.Mouse(0, 2, 20, 30, 5, false, Initial);
        Assert.Contains("MMV(20,30)", BasicRecorder.Serialize(buffer.Events, true));
    }

    [Fact]
    public async Task AcwZeroSizesPreserveNativeWindowDimensions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string title = "ACW-size-" + Guid.NewGuid();
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            nint previous = Native.SetThreadDpiAwarenessContext(-4);
            Assert.NotEqual(0, previous);
            try
            {
                Assert.True(Native.SetWindowPos(owner.Root, 0,
                    20, 30, 400, 300, 0x14));
            }
            finally
            {
                Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
            }
        }, title: title, dpiAwarenessContext: -4);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(title)], default);
        await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [id, Value.Numeric(-50), Value.Numeric(70),
                Value.Numeric(0), Value.Numeric(0), Value.Numeric(0)], default);
        Native.Rect bounds = PhysicalBounds();
        Assert.Equal((-50, 70, 400, 300),
            (bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        await session.InvokeAsync(BuiltinFunctionNames.Acw,
            [Value.Numeric(0), Value.Numeric(-60), Value.Numeric(80),
                Value.Numeric(450), Value.Numeric(0), Value.Numeric(0)], default);
        bounds = PhysicalBounds();
        Assert.Equal((-60, 80, 450, 300),
            (bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));

        Native.Rect PhysicalBounds()
        {
            nint previous = Native.SetThreadDpiAwarenessContext(-4);
            Assert.NotEqual(0, previous);
            try
            {
                Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect current));
                return current;
            }
            finally
            {
                Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
            }
        }
    }
}
