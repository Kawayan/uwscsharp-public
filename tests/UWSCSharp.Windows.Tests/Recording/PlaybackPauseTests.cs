using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class PlaybackPauseTests
{
    [Fact]
    public async Task WindowLookupFreezesTimeoutDuringPause()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        session.ConfigurePause(control, stop.Token);
        control.Pause();
        Task<Value> lookup = session.InvokeAsync("GETID",
            [Value.String(Guid.NewGuid().ToString()), Value.String(""), Value.Numeric(0.15)],
            stop.Token).AsTask();
        Assert.True(control.IsPaused);
        await Task.Delay(250, stop.Token);
        Assert.False(lookup.IsCompleted);
        control.Resume();
        await Task.Delay(30, stop.Token);
        Assert.False(lookup.IsCompleted);
        Assert.Equal(-1, (await lookup.WaitAsync(stop.Token)).AsNumber());
    }

    [Fact]
    public async Task DirectPhysicalModifierWaitCanPauseAndCancel()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var backend = new DirectInputTests.Backend { KeyState = -32768 };
        var direct = new DirectInputSession(backend);
        direct.ConfigurePause(control, stop.Token, () => !direct.HasHeldInput);
        control.Pause();
        Task sending = direct.KeyAsync(10, 65, 0, stop.Token).AsTask();
        Assert.True(control.IsPaused);
        Assert.Empty(backend.Messages);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Empty(backend.Messages);
    }

    [Fact]
    public async Task DirectModifiersAndButtonsPreventPauseUntilReleased()
    {
        var backend = new DirectInputTests.Backend();
        var direct = new DirectInputSession(backend);
        Assert.False(direct.HasHeldInput);
        await direct.KeyAsync(10, 16, 1, default);
        Assert.True(direct.HasHeldInput);
        await direct.KeyAsync(10, 16, 2, default);
        Assert.False(direct.HasHeldInput);
        await direct.MouseAsync(10, 0, 1, new(10, 10), default);
        Assert.True(direct.HasHeldInput);
        await direct.MouseAsync(10, 0, 2, new(10, 10), default);
        Assert.False(direct.HasHeldInput);
    }

    [Fact]
    public async Task OptFinallyInputDelayDoesNotPauseAfterCancellation()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inputs = new List<Native.Input>();
        Task run = new Interpreter(windows: new WindowsAutomation(inputs.Add))
            .RunAsync("OPTION OPTFINALLY\nTRY\nPRINT 1\nFINALLY\nKBD(65,CLICK,20)\nENDTRY",
                _ =>
                {
                    control.Pause();
                    stop.Cancel();
                }, stop.Token, new ScriptRunOptions([]) { PauseControl = control });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, inputs.Count);
    }

    [Fact]
    public async Task PauseWaitsForKeyUpWithoutReleasingAndRepressing()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inputs = new List<Native.Input>();
        var output = new List<string>();
        Task run = new Interpreter(windows: new WindowsAutomation(inputs.Add))
            .RunAsync("KBD(65,DOWN)\nPRINT 1\nSLEEP(0.05)\nKBD(65,UP)\nPRINT 2",
                text =>
                {
                    output.Add(text);
                    if (text == "1")
                    {
                        control.Pause();
                    }
                }, stop.Token, new ScriptRunOptions([]) { PauseControl = control });
        while (!control.IsPaused)
        {
            await Task.Delay(10, stop.Token);
        }

        Assert.Equal(["1"], output);
        Assert.Equal(2, inputs.Count);
        Assert.Equal(0u, inputs[0].Data.Keyboard.Flags);
        Assert.Equal(2u, inputs[1].Data.Keyboard.Flags);
        control.Resume();
        await run.WaitAsync(stop.Token);
        Assert.Equal(["1", "2"], output);
        Assert.Equal(2, inputs.Count);
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task MixedRecordingPausesAfterDragRelease()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var control = new ScriptPauseControl();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var movements = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        var clicks = System.Threading.Channels.Channel.CreateUnbounded<nint>();
        nint button = 0;
        nint next = 0;
        bool replaying = false;
        await using PumpedControlFixture target =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                button = owner.Child("BUTTON", "ドラッグ", 0, 10);
                next = owner.Child("BUTTON", "次の操作", 0, 50);
                owner.Clicked = (sender, _) => clicks.Writer.TryWrite(sender);
                owner.ObserveInput(button, (message, flags, _) =>
                {
                    if (!replaying)
                    {
                        return;
                    }

                    if (message is 0x201 or 0x203)
                    {
                        control.Pause();
                    }
                    else if (message == 0x200 && (flags & 1) != 0)
                    {
                        movements.Writer.TryWrite(control.IsPaused);
                    }
                });
            }, title: "高レベル記録-" + Guid.NewGuid().ToString("N"),
                dpiAwarenessContext: -4);
        await UiInputDriver.ActivateAsync(target.Root);
        Native.Point drag = default;
        Native.Point click = default;
        target.Invoke(() =>
        {
            Assert.True(Native.GetWindowRect(button, out Native.Rect first));
            Assert.True(Native.GetWindowRect(next, out Native.Rect second));
            drag = new((first.Left + first.Right) / 2,
                (first.Top + first.Bottom) / 2);
            click = new((second.Left + second.Right) / 2,
                (second.Top + second.Bottom) / 2);
        });
        using var recorder = new BasicRecorder(0, includeInjected: true)
        {
            RecordButtonClicks = true
        };
        await recorder.StartAsync().WaitAsync(deadline.Token);
        string gesture = FormattableString.Invariant(
            $"BTN(0,1,{drag.X},{drag.Y})\nSLEEP(0.15)\nMMV({drag.X + 8},{drag.Y})");
        gesture += FormattableString.Invariant(
            $"\nSLEEP(0.15)\nBTN(0,2)\nBTN(0,0,{click.X},{click.Y})");
        await new Interpreter(windows: new WindowsAutomation())
            .RunAsync(gesture, _ => { }, deadline.Token);
        Assert.Equal(button, await clicks.Reader.ReadAsync(deadline.Token));
        Assert.Equal(next, await clicks.Reader.ReadAsync(deadline.Token));
        string source = recorder.Stop();
        string[] commands = source.Split(Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["BTN", "MMV", "BTN"],
            commands.SkipWhile(line => !line.StartsWith("BTN(",
                    StringComparison.Ordinal))
                .Select(line => line.Split('(')[0])
                .Where(name => name is "BTN" or "MMV" or "CLKITEM")
                .Take(3));
        Assert.Contains(commands, line => line.StartsWith("SLEEP(",
            StringComparison.Ordinal));
        Assert.Contains("\"次の操作\"", commands[^1], StringComparison.Ordinal);

        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".uws");
        try
        {
            await File.WriteAllTextAsync(file, source, System.Text.Encoding.UTF8,
                deadline.Token);
            source = await File.ReadAllTextAsync(file, deadline.Token);
        }
        finally
        {
            File.Delete(file);
        }

        target.Invoke(() => replaying = true);
        var options = new ScriptRunOptions([]) { PauseControl = control };
        Task run = new Interpreter(windows: new WindowsAutomation())
            .RunAsync(source, _ => { }, deadline.Token, options);
        try
        {
            Assert.False(await movements.Reader.ReadAsync(deadline.Token));
            while (!control.IsPaused)
            {
                await Task.Delay(10, deadline.Token);
            }

            Assert.Equal(0, Native.GetAsyncKeyState(1) & 0x8000);
            Assert.Equal(button, await clicks.Reader.ReadAsync(deadline.Token));
            Assert.False(clicks.Reader.TryRead(out _));
            control.Resume();
            Assert.Equal(next, await clicks.Reader.ReadAsync(deadline.Token));
            await run.WaitAsync(deadline.Token);
            Assert.False(clicks.Reader.TryRead(out _));
        }
        finally
        {
            await deadline.CancelAsync();
            control.Resume();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // 検査失敗時も実行を回収します。
            }
        }
    }

    [Fact]
    public async Task CancelWhileHoldingInputReleasesIt()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inputs = new List<Native.Input>();
        Task run = new Interpreter(windows: new WindowsAutomation(inputs.Add))
            .RunAsync("KBD(65,DOWN)\nPRINT 1\nSLEEP(60)", _ =>
            {
                control.Pause();
                stop.Cancel();
            }, stop.Token, new ScriptRunOptions([]) { PauseControl = control });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, inputs.Count);
        Assert.Equal(2u, inputs[1].Data.Keyboard.Flags);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task WindowsArgumentDelayCanPauseAndCancelBeforeSending()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var inputs = new List<Native.Input>();
        await using IWindowsAutomationSession session =
            new WindowsAutomation(inputs.Add).CreateSession();
        session.ConfigurePause(control, stop.Token);
        control.Pause();
        Task sending = session.InvokeAsync("KBD",
            [Value.Numeric(65), Value.Numeric(0), Value.Numeric(500)],
            stop.Token).AsTask();
        Assert.True(control.IsPaused);
        Assert.Empty(inputs);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Empty(inputs);
    }
}
