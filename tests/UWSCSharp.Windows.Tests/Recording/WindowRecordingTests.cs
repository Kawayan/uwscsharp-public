using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class WindowRecordingTests(Xunit.Abstractions.ITestOutputHelper diagnostic)
{
    private static readonly RecordedWindow First =
        new(1, "文書", "Editor", -100, 20, 800, 600);

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void CheckboxCandidateRequiresMatchingChangedStateBeforeNextInput()
    {
        var candidate = new CheckboxRecordingCandidate();
        candidate.AwaitState(1, 1, 10, uint.MaxValue);
        Assert.False(candidate.CanObserve(1, uint.MaxValue - 1));
        Assert.True(candidate.CanObserve(1, uint.MaxValue));
        Assert.True(candidate.CanObserve(1, 0));
        Assert.Null(candidate.Observe(2, 0));
        Assert.Null(candidate.Observe(1, 1));
        Assert.Equal(10L, candidate.Observe(1, 0));
        Assert.Null(candidate.Observe(1, 0));

        candidate.AwaitState(1, 0, 20, 20);
        candidate.Discard();
        Assert.Null(candidate.Observe(1, 1));

        candidate.AwaitState(1, 1, 30, 30);
        Assert.Null(candidate.Observe(1, null));
        Assert.Null(candidate.Observe(1, 0));
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void CheckboxCandidateRejectsSharedReleaseTimeAfterDiscard()
    {
        var candidate = new CheckboxRecordingCandidate();
        candidate.AwaitState(1, 0, 10, uint.MaxValue);
        candidate.Discard();
        candidate.AwaitState(1, 1, 20, uint.MaxValue);
        Assert.False(candidate.CanObserve(1, uint.MaxValue));
        Assert.True(candidate.CanObserve(1, 0));
        Assert.Equal(20L, candidate.Observe(1, 0));
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task BackgroundCheckboxApiReplaysExplicitState()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string title = "背景再生チェックボックス-" + Guid.NewGuid().ToString("N");
        const int autoCheckbox = 3;
        const uint getCheck = 0xf0;
        nint button = 0;
        var notifications = new List<nint>();
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                button = owner.Child("BUTTON", "選択", autoCheckbox, 10);
                owner.Clicked = (sender, _) =>
                {
                    if (sender == button)
                    {
                        notifications.Add(Native.GetForegroundWindow());
                    }
                };
            }, title: title);
        await using PumpedControlFixture foreground =
            await PumpedControlFixture.CreateAsync(_ => { });
        await UiInputDriver.ActivateAsync(foreground.Root);
        int flags = (int)(ClickItemFlags.Button | ClickItemFlags.Background
            | ClickItemFlags.Api);
        foreach (int desired in new[] { 1, 1, 0 })
        {
            var output = new List<string>();
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(
                $"PRINT CLKITEM(GETID(\"{title}\"),\"選択\",{flags},{desired},1)",
                output.Add);
            fixture.Invoke(() =>
            {
                diagnostic.WriteLine(
                    $"要求={desired}、結果={string.Join(",", output)}、"
                    + $"状態={Native.SendMessageW(button, getCheck, 0, 0)}、"
                    + $"通知時の前面={string.Join(",", notifications)}、"
                    + $"現在の前面={Native.GetForegroundWindow()}、"
                    + $"期待する前面={foreground.Root}");
                Assert.Equal((nint)desired,
                    Native.SendMessageW(button, getCheck, 0, 0));
            });
            Assert.Equal("1", Assert.Single(output));
        }

        fixture.Invoke(() =>
        {
            Assert.Equal(2, notifications.Count);
            Assert.All(notifications, active => Assert.Equal(foreground.Root, active));
            Assert.Equal(foreground.Root, Native.GetForegroundWindow());
        });
    }

    [Fact]
    public async Task GeneratedScriptPreservesTitleAndBounds()
    {
        RecordedWindow window = First with
        {
            Title = "a\"<#CR>\r\nb"
        };
        var buffer = new RecordingBuffer([], 0);
        buffer.Key(65, 1, 1, false, window);
        buffer.Key(65, 2, 1, false);
        var target = new Target();
        await new Interpreter(windows: target).RunAsync(
            BasicRecorder.Serialize(buffer.Events), _ => { });
        Assert.Equal(
            ["GETID:a\"<#CR>\r\nb,Editor", "ACW:7,-100,20,800,600",
                "KBD:65,1", "KBD:65,2"], target.Calls);
    }

    [Fact]
    public void TargetAndBoundsChangesProduceAcwBeforeIndependentInput()
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Mouse(-1, 1, 0, 0, 1, false, First);
        buffer.Mouse(0, 1, 0, 0, 1, false, First);
        buffer.Mouse(0, 2, 0, 0, 1, false);
        buffer.Key(65, 1, 1, false, First);
        buffer.Key(65, 2, 1, false);
        buffer.Wheel(5, 120, 0, 0, 1, false, First with
        {
            X = 50
        });
        buffer.Wheel(5, 120, 0, 0, 1, false, First with
        {
            Handle = 2
        });
        buffer.Wheel(5, 120, 0, 0, 1, false, First);
        string source = BasicRecorder.Serialize(buffer.Events);
        Assert.StartsWith("MMV(0,0)" + Environment.NewLine + First.Command(), source);
        Assert.Equal(4, source.Split("ACW(").Length - 1);
        Assert.Contains("ACW(GETID(\"文書\",\"Editor\"),50,20,800,600)", source);
    }

    [Fact]
    public void HeldModifiersAndDraggingDoNotSwitchWindow()
    {
        var buffer = new RecordingBuffer([], 0);
        RecordedWindow other = First with
        {
            Handle = 2,
            Title = "別画面"
        };
        buffer.Key(160, 1, 1, false, First);
        buffer.Mouse(0, 1, 1, 2, 1, false, other);
        buffer.Mouse(-1, 1, 10, 20, 1, false, other);
        buffer.Key(65, 1, 1, false, other);
        buffer.Key(65, 2, 1, false);
        buffer.Mouse(0, 2, 10, 20, 1, false);
        buffer.Key(160, 2, 1, false);
        buffer.Key(66, 1, 1, false, other);
        string source = BasicRecorder.Serialize(buffer.Events.OrderBy(input => input.Sequence));
        Assert.StartsWith(First.Command(), source);
        Assert.Equal(2, source.Split("ACW(").Length - 1);
        Assert.Contains("KBD(160,2)" + Environment.NewLine
            + other.Command(), source);
    }

    [Fact]
    public void ExcludedAndInitialInputsDoNotLeaveWindowCommands()
    {
        var buffer = new RecordingBuffer([119], 4);
        buffer.Begin([65], []);
        buffer.Key(65, 2, 1, false, First);
        buffer.Key(160, 1, 1, false, First);
        buffer.Key(119, 1, 1, false, First);
        buffer.Key(119, 2, 1, false);
        buffer.Key(160, 2, 1, false);
        buffer.Mouse(0, 1, 0, 0, 1, true, First);
        buffer.Wheel(5, 120, 0, 0, 1, true, First);
        buffer.Complete();
        Assert.Empty(BasicRecorder.Serialize(buffer.Events));
    }

    [Fact]
    public void CaptureResolvesChildToRootAndSurvivesWindowDestruction()
    {
        nint root = Native.CreateWindowExW(0, "STATIC", "記録対象",
            0, -100, 20, 800, 600, 0, 0, 0, 0);
        Assert.NotEqual(0, root);
        RecordedWindow? captured;
        try
        {
            nint child = Native.CreateWindowExW(0, "STATIC", "子",
                0x40000000, 1, 2, 30, 40, root, 0, 0, 0);
            Assert.NotEqual(0, child);
            captured = RecordedWindow.Capture(child);
            Assert.NotNull(captured);
            Assert.Equal(root, captured.Handle);
            Assert.Equal("記録対象", captured.Title);
            Assert.Equal("Static", captured.ClassName, ignoreCase: true);
            using var dpi = new PhysicalDpiScope();
            Assert.True(Native.GetWindowRect(root, out Native.Rect rect));
            Assert.Equal(rect.Left, captured.X);
            Assert.Equal(rect.Bottom - rect.Top, captured.Height);
        }
        finally
        {
            Native.DestroyWindow(root);
        }

        Assert.Contains("記録対象", captured.Command());
        Assert.Null(RecordedWindow.Capture(0));
    }

    [Fact]
    [Trait("Category", "Headless")]
    public async Task RecordedButtonClickUsesCaptionAndOccurrenceWithoutCoordinates()
    {
        var click = new RecordedButtonClick(First, "実行\"<#CR>", 2);
        var target = new Target();
        await new Interpreter(windows: target).RunAsync(
            BasicRecorder.Serialize(
                [new(2, 0, 1, 123, 456, 0, ButtonClick: click)]), _ => { });

        Assert.Equal(
            ["GETID:文書,Editor", "CLKITEM:7,実行\"<#CR>,1,1,2"],
            target.Calls);
    }

    [Fact]
    public async Task CapturesNativePushButtonInReplayOrder()
    {
        nint lower = 0;
        nint upper = 0;
        nint check = 0;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(host =>
            {
                lower = host.Child("BUTTON", "実行", 0, 60);
                upper = host.Child("BUTTON", "実行", 0, 10);
                check = host.Child("BUTTON", "選択", 3, 100);
            });

        fixture.Invoke(() =>
        {
            Assert.Equal(1, Assert.IsType<RecordedButtonClick>(
                RecordedButtonClick.Capture(upper)).Occurrence);
            RecordedButtonClick click = Assert.IsType<RecordedButtonClick>(
                RecordedButtonClick.Capture(lower));
            Assert.Equal(2, click.Occurrence);
            Assert.Equal("実行", click.Caption);
            Assert.Null(RecordedButtonClick.Capture(check));
            var buffer = new RecordingBuffer([], 0);
            BasicRecorder.RecordMouse(buffer, 0x201, 0, 10, 60, 1, false,
                buttonTarget: lower);
            BasicRecorder.RecordMouse(buffer, 0x202, 0, 10, 60, 2, false,
                buttonTarget: lower);
            Assert.Equal(click.Command() + Environment.NewLine,
                BasicRecorder.Serialize(buffer.Events));

            var timed = new RecordingBuffer([], 0);
            timed.Mouse(0, 1, 10, 60, 1000, false, buttonTarget: lower);
            timed.Mouse(0, 2, 10, 60, 1200, false, buttonTarget: lower);
            timed.Mouse(0, 1, 10, 60, 1400, false, buttonTarget: lower);
            timed.Mouse(0, 2, 10, 60, 1500, false, buttonTarget: lower);
            Assert.Equal(
                [click.Command(), "SLEEP(0.3)", click.Command()],
                BasicRecorder.Serialize(timed.Events).Split(
                    Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));

            var drag = new RecordingBuffer([], 0);
            BasicRecorder.RecordMouse(drag, 0x201, 0, 10, 60, 1, false,
                buttonTarget: lower);
            BasicRecorder.RecordMouse(drag, 0x202, 0, 11, 60, 2, false,
                buttonTarget: lower);
            Assert.Equal(2, drag.Events.Count);
            Assert.All(drag.Events, input => Assert.Null(input.ButtonClick));
        });
    }

    [Theory]
    [InlineData("UWSCSharp.Cli", false)]
    [InlineData("UWSCSharp.Wpf", false)]
    [InlineData("UWSCSharp.Cli", true)]
    [InlineData("UWSCSharp.Wpf", true)]
    [Trait("Category", "SandboxOnly")]
    public async Task RecordsAndReplaysNativeButtonClick(
        string frontend, bool background)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string title = "記録ボタン-" + Guid.NewGuid().ToString("N");
        var recorded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replayed = new TaskCompletionSource<nint>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        nint button = 0;
        int notifications = 0;
        int otherNotifications = 0;
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                button = owner.Child("BUTTON", "実行", 0, 65);
                nint other = owner.Child("BUTTON", "実行", 0, 10);
                owner.Clicked = (sender, _) =>
                {
                    if (sender == other)
                    {
                        Interlocked.Increment(ref otherNotifications);
                    }

                    if (sender == button && ++notifications == 1)
                    {
                        recorded.TrySetResult();
                    }
                    else if (sender == button)
                    {
                        replayed.TrySetResult(Native.GetForegroundWindow());
                    }
                };
            }, title: title, dpiAwarenessContext: -4);
        await UiInputDriver.ActivateAsync(fixture.Root);

        using var recorder = new BasicRecorder(0, includeInjected: true)
        {
            RecordButtonClicks = !background,
            BackgroundPlayback = background,
            OmitIdleTimeAndMouseMoves = true
        };
        fixture.Invoke(() =>
        {
            Assert.True(Native.GetWindowRect(button, out Native.Rect bounds));
            var point = new Native.Point((bounds.Left + bounds.Right) / 2,
                (bounds.Top + bounds.Bottom) / 2);
            Assert.Equal(button, Native.WindowFromPoint(point));
            Assert.True(Native.SetCursorPos(point.X, point.Y));
        });
        await recorder.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Invoke(() =>
        {
            foreach (uint flags in new uint[] { 2, 4 })
            {
                NativeInputSender.Send(new Native.Input
                {
                    Type = 0,
                    Data = new Native.InputUnion
                    {
                        Mouse = new Native.MouseInput { Flags = flags }
                    }
                });
            }
        });
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string source = recorder.Stop();
        Assert.True(source.StartsWith("CLKITEM(GETID(", StringComparison.Ordinal), source);
        Assert.EndsWith(",1,2)" + Environment.NewLine, source);
        Assert.Single(source.Split(Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries));

        int foregroundNotifications = 0;
        await using PumpedControlFixture? foreground = background
            ? await PumpedControlFixture.CreateAsync(owner =>
            {
                owner.Child("BUTTON", "実行", 0, 10);
                owner.Child("BUTTON", "実行", 0, 65);
                owner.Clicked = (_, _) =>
                    Interlocked.Increment(ref foregroundNotifications);
            }) : null;
        if (foreground is not null)
        {
            fixture.Invoke(() =>
            {
                Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect before));
                Assert.True(Native.SetWindowPos(fixture.Root, 0,
                    before.Left + 100, before.Top + 100, 0, 0, 0x15));
                Assert.True(Native.GetWindowRect(fixture.Root, out Native.Rect after));
                Assert.Equal((before.Left + 100, before.Top + 100),
                    (after.Left, after.Top));
            });
            await UiInputDriver.ActivateAsync(foreground.Root);

            using var cancellation = new CancellationTokenSource();
            var waiting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task playback = new Interpreter(windows: new WindowsAutomation()).RunAsync(
                "PRINT 1\nSLEEP(30)\n" + source, _ => waiting.TrySetResult(),
                cancellation.Token);
            try
            {
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                await cancellation.CancelAsync();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => playback.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(replayed.Task.IsCompleted);
            Assert.Equal(foreground.Root, Native.GetForegroundWindow());
        }

        string file = Path.Combine(Path.GetTempPath(),
            Guid.NewGuid().ToString("N") + ".uws");
        try
        {
            const string foregroundProbe = "PRINT IDTOHND(GETID(GET_ACTIVE_WIN))";
            string script = background
                ? foregroundProbe + Environment.NewLine + source + foregroundProbe
                : source;
            await File.WriteAllTextAsync(file, script, System.Text.Encoding.UTF8);
            string executable = FixturePaths.Get(frontend, frontend + ".exe");
            (int code, string output) = await SafeArrayFrontendTests.RunAsync(
                executable, [file]);
            Assert.True(code == 0, output);
            nint active = await replayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref otherNotifications));
            if (foreground is not null)
            {
                string[] observations = output.Split(
                    Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(2, observations.Length);
                diagnostic.WriteLine(
                    $"前面ハンドル: 起動前={foreground.Root}, 対象={fixture.Root}, "
                    + $"通知前={observations[0]}, 通知時={active}, 通知後={observations[1]}");
                Assert.Equal(foreground.Root, active);

                await fixture.DisposeAsync();
                Assert.False(Native.IsWindow(fixture.Root));
                Assert.Equal(foreground.Root, Native.GetForegroundWindow());
                await File.WriteAllTextAsync(file, "PRINT " + source,
                    System.Text.Encoding.UTF8);
                (int missingCode, string missingOutput) =
                    await SafeArrayFrontendTests.RunAsync(executable, [file]);
                Assert.True(missingCode == 0, missingOutput);
                Assert.Equal("0", missingOutput.Trim());
                foreground.Invoke(() =>
                    Assert.Equal(0, Volatile.Read(ref foregroundNotifications)));
                Assert.Equal(foreground.Root, Native.GetForegroundWindow());

                var replacementClick = new TaskCompletionSource<nint>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                nint replacementButton = 0;
                int wrongReplacementClicks = 0;
                await using PumpedControlFixture replacement =
                    await PumpedControlFixture.CreateAsync(owner =>
                    {
                        replacementButton = owner.Child("BUTTON", "実行", 0, 65);
                        owner.Child("BUTTON", "実行", 0, 10);
                        owner.Clicked = (sender, _) =>
                        {
                            if (sender == replacementButton)
                            {
                                replacementClick.TrySetResult(
                                    Native.GetForegroundWindow());
                            }
                            else
                            {
                                Interlocked.Increment(ref wrongReplacementClicks);
                            }
                        };
                    }, title: title, dpiAwarenessContext: -4);
                Assert.NotEqual(fixture.Root, replacement.Root);
                await UiInputDriver.ActivateAsync(foreground.Root);
                (int reopenedCode, string reopenedOutput) =
                    await SafeArrayFrontendTests.RunAsync(executable, [file]);
                Assert.True(reopenedCode == 0, reopenedOutput);
                Assert.Equal("1", reopenedOutput.Trim());
                Assert.Equal(foreground.Root, await replacementClick.Task
                    .WaitAsync(TimeSpan.FromSeconds(5)));
                replacement.Invoke(() =>
                    Assert.Equal(0, Volatile.Read(ref wrongReplacementClicks)));
                foreground.Invoke(() =>
                    Assert.Equal(0, Volatile.Read(ref foregroundNotifications)));
                Assert.Equal(foreground.Root, Native.GetForegroundWindow());
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Headless")]
    public void IncompleteButtonClickPreservesBalancedLowLevelInput(bool differentTarget)
    {
        var buffer = new RecordingBuffer([], 0);
        buffer.Mouse(0, 1, 10, 60, 1000, false, buttonTarget: 1);
        if (differentTarget)
        {
            buffer.Mouse(0, 2, 10, 60, 1200, false, buttonTarget: 2);
        }

        buffer.Complete();
        string[] lines = BasicRecorder.Serialize(buffer.Events, true)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["BTN(0,1,10,60)",
            differentTarget ? "BTN(0,2,10,60)" : "BTN(0,2)"], lines);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void HighLevelRecordingKeepsKeyboardRightClickAndDrag()
    {
        var buffer = new RecordingBuffer([], 0)
        {
            RecordButtonClicks = true,
            OmitIdleTimeAndMouseMoves = true
        };
        buffer.Key(65, 1, 100, false);
        buffer.Key(65, 2, 110, false);
        buffer.Mouse(1, 1, 10, 20, 120, false);
        buffer.Mouse(1, 2, 10, 20, 130, false);
        buffer.Mouse(0, 1, 10, 20, 140, false, buttonTarget: 1);
        buffer.Mouse(-1, 1, 15, 25, 150, false);
        buffer.Mouse(-1, 1, 20, 30, 160, false);
        buffer.Mouse(0, 2, 20, 30, 170, false, buttonTarget: 1);
        buffer.Complete();

        string source = BasicRecorder.Serialize(buffer.Events, true);
        Assert.Equal([
            "KBD(65,1)", "KBD(65,2)", "BTN(1,1,10,20)", "BTN(1,2,10,20)",
            "BTN(0,1,10,20)", "MMV(15,25)", "MMV(20,30)", "BTN(0,2,20,30)"
        ], source.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    [Trait("Category", "Headless")]
    public async Task BackgroundRecordingEmitsOnlyTargetedButtonCommands()
    {
        var click = new RecordedButtonClick(First, "実行", 1);
        var uncheckedButton = new RecordedButtonClick(First, "選択", 2, State: 0);
        var target = new Target();
        string source = BasicRecorder.Serialize(
            [new(0, 65, 1, 0, 0, 0), new(2, 0, 1, 10, 10, 1),
                new(2, 0, 1, 20, 20, 2, ButtonClick: click),
                new(2, 0, 1, 20, 20, 2, ButtonClick: uncheckedButton)],
            backgroundPlayback: true);
        await new Interpreter(windows: target).RunAsync(source, _ => { });

        int mode = (int)(ClickItemFlags.Button | ClickItemFlags.Background
            | ClickItemFlags.Api);
        Assert.Equal(["GETID:文書,Editor", $"CLKITEM:7,実行,{mode},1,1",
            "GETID:文書,Editor", $"CLKITEM:7,選択,{mode},0,2"],
            target.Calls);
    }

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [Trait("Category", "SandboxOnly")]
    public async Task RecordingReplaysButtonGesture(
        bool drag, bool relative, bool compact, bool outside = false)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var clicks = System.Threading.Channels.Channel.CreateUnbounded<nint>();
        List<(uint Message, nint Coordinates)> received = [];
        nint button = 0;
        Native.Point start = default;
        Native.Point outsidePoint = default;
        await using PumpedControlFixture target =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                button = owner.Child("BUTTON", "実行", 0, 10);
                owner.Clicked = (sender, _) => clicks.Writer.TryWrite(sender);
                owner.ObserveInput(button, (message, flags, coordinates) =>
                {
                    if (message is 0x201 or 0x202 or 0x203
                        || (message == 0x200 && (flags & 1) != 0))
                    {
                        // 直前のクリックをOSがダブルクリックと判定する場合も押下として扱います。
                        received.Add((message == 0x203 ? 0x201u : message, coordinates));
                    }
                });
            }, title: "省略記録-" + Guid.NewGuid().ToString("N"),
            dpiAwarenessContext: -4);
        await UiInputDriver.ActivateAsync(target.Root);
        using var recorder = new BasicRecorder(0, includeInjected: true)
        {
            OmitIdleTimeAndMouseMoves = compact,
            RelativeMouseCoordinates = relative
        };
        await recorder.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        target.Invoke(() =>
        {
            Assert.Equal(target.Root, Native.GetForegroundWindow());
            Assert.True(Native.GetWindowRect(button, out Native.Rect bounds));
            int x = (bounds.Left + bounds.Right) / 2;
            int y = (bounds.Top + bounds.Bottom) / 2;
            Assert.True(Native.GetWindowRect(target.Root, out Native.Rect root));
            start = new(x, y);
            outsidePoint = new(root.Left - 10, root.Top - 10);
            Assert.True(Native.SetCursorPos(x - 10, y));
            Assert.True(Native.SetCursorPos(x, y));
            Assert.Equal(button, Native.WindowFromPoint(new Native.Point(x, y)));
        });
        await Task.Delay(150);
        if (outside)
        {
            await new Interpreter(windows: new WindowsAutomation()).RunAsync(
                FormattableString.Invariant(
                    $"BTN(0,1,{start.X},{start.Y})\nMMV({outsidePoint.X},{outsidePoint.Y})\nMMV({start.X},{start.Y})\nBTN(0,2)"),
                _ => { });
        }
        else
        {
            uint[] gesture = drag ? [2, 1, 4] : [2, 4];
            foreach (uint flags in gesture)
            {
                if (!compact && flags == 4)
                {
                    await Task.Delay(150);
                }

                NativeInputSender.Send(new Native.Input
                {
                    Type = 0,
                    Data = new Native.InputUnion
                    {
                        Mouse = new Native.MouseInput
                        {
                            Flags = flags,
                            X = flags == 1 ? 8 : 0
                        }
                    }
                });
            }
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(button, await clicks.Reader.ReadAsync(deadline.Token));
        string source = recorder.Stop();
        (uint Message, nint Coordinates)[] recorded = [];
        target.Invoke(() =>
        {
            recorded = [.. received.Distinct()];
            received.Clear();
        });
        uint[] expectedMessages = outside ? [0x201u, 0x200u, 0x200u, 0x202u]
            : drag ? [0x201u, 0x200u, 0x202u] : [0x201u, 0x202u];
        Assert.Equal(expectedMessages,
            recorded.Select(input => input.Message));
        if (drag)
        {
            Assert.NotEqual(recorded[0].Coordinates, recorded[1].Coordinates);
        }

        if (compact)
        {
            Assert.DoesNotContain("SLEEP(", source, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("SLEEP(", source, StringComparison.Ordinal);
        }

        string[] mouseCommands = [.. source.Split(Environment.NewLine)
            .Where(line => line.StartsWith("BTN(", StringComparison.Ordinal)
                || line.StartsWith("MMV(", StringComparison.Ordinal))
            .Select(line => line.Split('(')[0])];
        if (!compact)
        {
            mouseCommands = [.. mouseCommands.SkipWhile(command => command == "MMV")];
        }

        string[] expectedCommands = outside ? ["BTN", "MMV", "MMV", "BTN"]
            : drag ? ["BTN", "MMV", "BTN"] : ["BTN", "BTN"];
        Assert.Equal(expectedCommands, mouseCommands);
        if (outside)
        {
            Assert.Contains("MMV(STATUS(UWSC_RECORD_WINDOW_1,ST_X)+(-10),"
                + "STATUS(UWSC_RECORD_WINDOW_1,ST_Y)+(-10))", source);
        }

        if (relative)
        {
            Assert.Contains("STATUS(UWSC_RECORD_WINDOW_1,ST_X)", source);
            Assert.DoesNotContain("MOUSEORG(", source, StringComparison.Ordinal);
            target.Invoke(() =>
            {
                Assert.True(Native.GetWindowRect(target.Root,
                    out Native.Rect before));
                const uint noResizeNoReorderNoActivate = 0x15;
                Assert.True(Native.SetWindowPos(target.Root, 0,
                    before.Left + 160, before.Top + 100, 0, 0,
                    noResizeNoReorderNoActivate));
                Assert.True(Native.GetWindowRect(target.Root,
                    out Native.Rect after));
                Assert.Equal((before.Left + 160, before.Top + 100),
                    (after.Left, after.Top));
            });
        }

        await new Interpreter(windows: new WindowsAutomation())
            .RunAsync(source, _ => { }, deadline.Token);
        Assert.Equal(button, await clicks.Reader.ReadAsync(deadline.Token));
        target.Invoke(() => Assert.Equal(recorded, received.Distinct()));
    }

    private sealed class Target : IWindowsAutomation, IWindowsAutomationSession
    {
        public List<string> Calls { get; } = [];

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Calls.Add(name + ":" + string.Join(",",
                arguments.Select(value => value.Display())));
            return ValueTask.FromResult(name == "GETID"
                ? Value.Numeric(7) : new Value(ValueKind.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
