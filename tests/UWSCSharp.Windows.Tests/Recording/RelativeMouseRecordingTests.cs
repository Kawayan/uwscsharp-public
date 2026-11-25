using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class RelativeMouseRecordingTests
{
    private static readonly RecordedWindow Window =
        new(1, "文書", "Editor", -100, 20, 800, 600);

    [Fact]
    public async Task ActiveWindowChangesDuringDragUseEachInputsReference()
    {
        RecordedWindow active = Window with
        {
            Handle = 2,
            Title = "別画面",
            X = 200,
            Y = 300
        };
        var buffer = new RecordingBuffer([], 0);
        buffer.Mouse(0, 1, -90, 30, 1, false, Window);
        // ポインター位置は同じでも、入力時点のアクティブ画面が変わります。
        buffer.Mouse(-1, 1, -90, 30, 1, false, active);
        buffer.Mouse(0, 2, -90, 30, 1, false, active);
        string source = BasicRecorder.Serialize(buffer.Events, true, true);
        var target = new RelocatedTarget();
        await new Interpreter(windows: target).RunAsync(source, _ => { });
        Assert.Equal(["BTN:510,410", "MMV:1210,1130", "BTN:1210,1130"], target.Positions);
        Assert.Equal(1, source.Split("ACW(").Length - 1);
        Assert.Contains("STATUS(UWSC_RECORD_WINDOW_2,ST_X)+(-290)", source);
        Assert.DoesNotContain("MOUSEORG", source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelativeClickReplaysAtRelocatedWindow(bool compact)
    {
        var buffer = new RecordingBuffer([], 0)
        {
            OmitIdleTimeAndMouseMoves = compact
        };
        buffer.Mouse(-1, 1, -90, 40, 1, false, Window);
        buffer.Mouse(0, 1, -70, 60, 1, false, Window);
        buffer.Mouse(0, 2, -70, 60, 1, false, Window);
        buffer.Wheel(5, 120, -50, 80, 1, false, Window);
        string source = BasicRecorder.Serialize(buffer.Events, compact, true);
        Assert.Contains("DIM UWSC_RECORD_WINDOW_1 = GETID(\"文書\",\"Editor\")", source);
        Assert.Contains("ACW(UWSC_RECORD_WINDOW_1)", source);
        Assert.DoesNotContain("MOUSEORG", source);
        Assert.DoesNotContain(
            "ACW(GETID(\"文書\",\"Editor\"),-100", source);
        var target = new RelocatedTarget();
        await new Interpreter(windows: target).RunAsync(source, _ => { });
        Assert.Equal(compact
            ? ["BTN:530,440", "BTN:530,440", "BTN:550,460"]
            : new[] { "MMV:510,420", "BTN:530,440", "BTN:530,440", "BTN:550,460" },
            target.Positions);
        Assert.DoesNotContain("MOUSEORG", source);
    }

    [Fact]
    public void DragUsesCurrentBoundsWithoutInsertingAcw()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Mouse(0, 1, -90, 30, 1, false, Window);
        buffer.Mouse(1, 1, -90, 30, 1, false, Window);
        RecordedWindow moved = Window with
        {
            X = -50,
            Y = 40
        };
        buffer.Mouse(-1, 1, -40, 50, 1, false, moved);
        buffer.Mouse(0, 2, -40, 50, 1, false, moved);
        buffer.Mouse(1, 2, -40, 50, 1, false, moved);
        string source = BasicRecorder.Serialize(buffer.Events, true, true);
        Assert.Contains("MMV(STATUS(UWSC_RECORD_WINDOW_1,ST_X)+(10),STATUS(UWSC_RECORD_WINDOW_1,ST_Y)+(10))", source);
        Assert.Contains("BTN(0,2,STATUS(UWSC_RECORD_WINDOW_1,ST_X)+(10)", source);
        Assert.Contains("BTN(1,2,STATUS(UWSC_RECORD_WINDOW_1,ST_X)+(10)", source);
        Assert.DoesNotContain("MOUSEORG", source);
        Assert.Equal(1, source.Split("ACW(").Length - 1);
    }

    [Fact]
    public void UnknownTargetFallsBackToScreenCoordinatesAndNextTargetRestoresOrigin()
    {
        RecordedWindow other = Window with
        {
            Handle = 2,
            Title = "別画面",
            X = 200
        };
        string source = BasicRecorder.Serialize(
        [
            new(RecordedInputKind.MouseMove, 0, 1, -90, 30, 1, Window: Window),
            new(RecordedInputKind.MouseMove, 0, 1, 900, 800, 1),
            new(RecordedInputKind.Wheel, 6, 120, 210, 30, 1, Window: other)
        ], relativeMouseCoordinates: true);
        Assert.Contains("MMV(900,800)", source);
        Assert.DoesNotContain("MOUSEORG", source);
        Assert.Contains("DIM UWSC_RECORD_WINDOW_2 = GETID(\"別画面\",\"Editor\")", source);
        Assert.Contains("BTN(6,1,STATUS(UWSC_RECORD_WINDOW_2,ST_X)+(10)", source);
    }

    [Fact]
    public void RecoveryReleasesDragWithoutChangingCoordinateMode()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Mouse(0, 1, -90, 30, 1, false, Window);
        buffer.Recover(1, [], []);
        string source = BasicRecorder.Serialize(buffer.Events, true, true);
        Assert.EndsWith("BTN(0,2)" + Environment.NewLine, source);
        Assert.DoesNotContain("MOUSEORG", source);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "SandboxOnly")]
    public async Task MissingRelativeTargetStopsBeforePointerInput(
        bool otherWindowIsActive, bool compact)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        RecordedWindow? captured = null;
        nint root;
        await using (PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { },
                title: "相対座標対象-" + Guid.NewGuid().ToString("N")))
        {
            root = fixture.Root;
            fixture.Invoke(() => captured = RecordedWindow.Capture(root));
        }

        Assert.NotNull(captured);
        Assert.False(Native.IsWindow(root));
        await using PumpedControlFixture? other = otherWindowIsActive
            ? await PumpedControlFixture.CreateAsync(_ => { },
                title: "別の相対座標対象-" + Guid.NewGuid().ToString("N"))
            : null;
        if (other is not null)
        {
            await UiInputDriver.ActivateAsync(other.Root);
            Assert.Equal(other.Root, Native.GetForegroundWindow());
        }

        string source = BasicRecorder.Serialize(
        [
            new(RecordedInputKind.MouseMove, 0, 1, captured.X + 30, captured.Y + 30, 1,
                Window: captured),
            new(RecordedInputKind.MouseButton, 0, 1, captured.X + 30, captured.Y + 30, 2,
                Window: captured),
            new(RecordedInputKind.MouseButton, 0, 2, captured.X + 30, captured.Y + 30, 3,
                Window: captured)
        ], compact, relativeMouseCoordinates: true);
        Assert.Contains("BTN(", source);
        Assert.True(Native.GetPhysicalCursorPos(out Native.Point before));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        List<string> output = [];
        int exitCode = await new Interpreter(windows: new WindowsAutomation())
            .RunWithExitCodeAsync(source + "PRINT 99",
                new CallbackScriptHost(output.Add), deadline.Token, ScriptRunOptions.Empty);
        Assert.Equal(1, exitCode);
        Assert.True(Native.GetPhysicalCursorPos(out Native.Point after));
        Assert.Empty(output);
        Assert.Equal((before.X, before.Y), (after.X, after.Y));
        if (other is not null)
        {
            Assert.Equal(other.Root, Native.GetForegroundWindow());
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [Trait("Category", "SandboxOnly")]
    public async Task RelativeTargetDestroyedDuringReplayStopsBeforeNextMove(
        int button, int virtualKey)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        nint target = 0;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
                target = owner.Child("BUTTON", "実行", 0, 10),
                title: "再生中の相対座標対象-" + Guid.NewGuid().ToString("N"),
                dpiAwarenessContext: -4);
        RecordedWindow? origin = null;
        Native.Point center = default;
        fixture.Invoke(() =>
        {
            origin = RecordedWindow.Capture(fixture.Root);
            Assert.True(Native.GetWindowRect(target, out Native.Rect bounds));
            center = new((bounds.Left + bounds.Right) / 2,
                (bounds.Top + bounds.Bottom) / 2);
        });
        Assert.NotNull(origin);
        bool buttonHeld = button >= 0;
        BasicRecorder.InputEvent[] events = buttonHeld
            ? [new(RecordedInputKind.MouseButton, button, 1, center.X, center.Y, 1, Window: origin),
                new(RecordedInputKind.MouseMove, 0, 1, center.X + 8, center.Y, 1, Window: origin),
                new(RecordedInputKind.MouseButton, button, 2, center.X + 8, center.Y, 1, Window: origin)]
            : [new(RecordedInputKind.MouseMove, 0, 1, center.X, center.Y, 1, Window: origin),
                new(RecordedInputKind.MouseMove, 0, 1, center.X + 8, center.Y, 1, Window: origin)];
        string source = BasicRecorder.Serialize(events,
            relativeMouseCoordinates: true);
        int firstMove = source.IndexOf(buttonHeld ? "BTN(" : "MMV(",
            StringComparison.Ordinal);
        Assert.True(firstMove >= 0);
        int afterFirstMove = source.IndexOf('\n', firstMove) + 1;
        Assert.True(afterFirstMove > firstMove);
        source = source.Insert(afterFirstMove, "PRINT 1\n") + "PRINT 2";

        List<string> output = [];
        Native.Point before = default;
        var host = new CallbackScriptHost(text =>
        {
            output.Add(text);
            if (text == "1")
            {
                Assert.Equal(buttonHeld,
                    (Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0);
                fixture.Dispose();
                Assert.False(Native.IsWindow(fixture.Root));
                Assert.True(Native.GetPhysicalCursorPos(out before));
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int exitCode = await new Interpreter(windows: new WindowsAutomation())
            .RunWithExitCodeAsync(source, host, deadline.Token, ScriptRunOptions.Empty);
        Assert.Equal(1, exitCode);
        Assert.Equal(["1"], output);
        Assert.True(Native.GetPhysicalCursorPos(out Native.Point after));
        Assert.Equal((before.X, before.Y), (after.X, after.Y));
        Assert.Equal(0, Native.GetAsyncKeyState(virtualKey) & 0x8000);
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task CancellingRelativeDragReleasesHeldButton()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var input = System.Threading.Channels.Channel.CreateUnbounded<uint>();
        nint button = 0;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                button = owner.Child("BUTTON", "実行", 0, 10);
                owner.ObserveInput(button, (message, _, _) =>
                {
                    if (message is 0x201 or 0x202)
                    {
                        input.Writer.TryWrite(message);
                    }
                });
            }, title: "相対ドラッグ-" + Guid.NewGuid().ToString("N"),
                dpiAwarenessContext: -4);
        await UiInputDriver.ActivateAsync(fixture.Root);
        RecordedWindow? origin = null;
        Native.Point center = default;
        fixture.Invoke(() =>
        {
            origin = RecordedWindow.Capture(fixture.Root);
            Assert.True(Native.GetWindowRect(button, out Native.Rect bounds));
            center = new((bounds.Left + bounds.Right) / 2,
                (bounds.Top + bounds.Bottom) / 2);
        });
        Assert.NotNull(origin);

        var buffer = new RecordingBuffer([], 0);
        buffer.Mouse(0, 1, center.X, center.Y, 1, false, origin);
        buffer.Mouse(-1, 1, center.X + 8, center.Y, 30_001, false, origin);
        buffer.Mouse(0, 2, center.X + 8, center.Y, 30_001, false, origin);
        string source = BasicRecorder.Serialize(buffer.Events,
            relativeMouseCoordinates: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task playback = new Interpreter(windows: new WindowsAutomation())
            .RunAsync(source, _ => { }, stop.Token);
        try
        {
            Assert.Equal(0x201u, await input.Reader.ReadAsync(deadline.Token));
            Assert.NotEqual(0, Native.GetAsyncKeyState(1) & 0x8000);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => playback.WaitAsync(deadline.Token));
        }

        Assert.Equal(0x202u, await input.Reader.ReadAsync(deadline.Token));
        Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
    }

    private sealed class RelocatedTarget : IWindowsAutomation, IWindowsAutomationSession
    {
        public List<string> Positions { get; } = [];

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            if (name == "STATUS")
            {
                if (arguments[1].AsNumber() == (int)WindowStatusItem.IsId)
                {
                    return ValueTask.FromResult(Value.Numeric(1));
                }

                double shift = arguments[0].AsNumber() == 8 ? 1000 : 0;
                return ValueTask.FromResult(Value.Numeric(shift + (arguments[1].AsNumber() == 1 ? 500 : 400)));
            }
            else if (name is "MMV" or "BTN")
            {
                int offset = name == "MMV" ? 0 : 2;
                if (arguments.Count > offset + 1)
                {
                    double x = arguments[offset].AsNumber();
                    double y = arguments[offset + 1].AsNumber();
                    Positions.Add($"{name}:{x},{y}");
                }
            }

            return ValueTask.FromResult(name == "GETID"
                ? Value.Numeric(arguments[0].Display() == "別画面" ? 8 : 7) : new Value(ValueKind.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
