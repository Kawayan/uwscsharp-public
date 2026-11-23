using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class PauseTests
{
    [Fact]
    public async Task WaitingDoesNotBlockCallerAndCancellationNeedsNoResume()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        control.Pause();
        Task waiting = control.WaitAsync(stop.Token).AsTask();
        Assert.True(control.IsPaused);
        Assert.False(waiting.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task RepeatedPauseResumeDoesNotLoseSignals()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 100; i++)
        {
            control.Pause();
            Task waiting = control.WaitAsync(stop.Token).AsTask();
            control.Pause();
            control.Resume();
            control.Resume();
            await waiting.WaitAsync(stop.Token);
        }

        Assert.False(control.IsPauseRequested);
        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task PausedRunResumesWithoutLosingState()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        control.Pause();
        var output = new List<string>();
        var run = Task.Run(() => new Interpreter().RunAsync(
            "x = 40\nx = x + 2\nPRINT x", output.Add,
            stop.Token, new ScriptRunOptions([]) { PauseControl = control }));
        await WaitPaused(control, stop.Token);
        Assert.Empty(output);
        Assert.False(run.IsCompleted);
        control.Resume();
        await run.WaitAsync(stop.Token);
        Assert.Equal(["42"], output);
    }

    [Fact]
    public async Task OutputFreeLoopCanPauseAndCancel()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource();
        var run = Task.Run(() => new Interpreter().RunAsync(
            "PRINT 1\nWHILE TRUE\nWEND", _ => started.SetResult(),
            stop.Token, new ScriptRunOptions([]) { PauseControl = control }));
        await started.Task.WaitAsync(stop.Token);
        control.Pause();
        await WaitPaused(control, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CancelWhilePausedStillRunsOptFinally()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var output = new List<string>();
        var run = Task.Run(() => new Interpreter().RunAsync(
            "OPTION OPTFINALLY\nTRY\nPRINT 1\nWHILE TRUE\nWEND\nFINALLY\nPRINT 2\nENDTRY",
            text =>
            {
                output.Add(text);
                if (text == "1")
                {
                    control.Pause();
                }
            }, stop.Token,
            new ScriptRunOptions([]) { PauseControl = control }));
        await WaitPaused(control, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(["1", "2"], output);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task SleepDoesNotFinishDuringPause()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var host = new HeldSleepHost(control);
        var run = Task.Run(() => new Interpreter().RunAsync(
            "PRINT 1\nSLEEP(0.2)\nPRINT 2", host, stop.Token,
            new ScriptRunOptions([]) { PauseControl = control }));
        try
        {
            await host.Started.Task.WaitAsync(stop.Token);
            await WaitPaused(control, stop.Token);
            await Task.Delay(300, stop.Token);
            Assert.False(run.IsCompleted);
            Assert.False(host.DelayStarted.Task.IsCompleted);
            control.Resume();
            await host.DelayStarted.Task.WaitAsync(stop.Token);
            Assert.False(run.IsCompleted);
            host.Release.TrySetResult();
            await run.WaitAsync(stop.Token);
            Assert.Equal(["1", "2"], host.Output);
        }
        finally
        {
            host.Release.TrySetResult();
            stop.Cancel();
            control.Resume();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
                // 検査失敗時も、一時停止と開始済みの待機を回収します。
            }
        }
    }

    private static async Task WaitPaused(ScriptPauseControl control,
        CancellationToken token)
    {
        while (!control.IsPaused)
        {
            await Task.Delay(10, token);
        }
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task PauseRequestedInsideSleepPreservesRemainingDelay()
    {
        using var control = new ScriptPauseControl();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clock = new ManualClock();
        int calls = 0;
        TimeSpan elapsed = TimeSpan.Zero;
        var run = Task.Run(() => control.DelayAsync(
            TimeSpan.FromMilliseconds(200), stop.Token, DelayAsync,
            timeProvider: clock));
        await WaitPaused(control, stop.Token);
        Assert.False(run.IsCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(10), elapsed);
        control.Resume();
        await run.WaitAsync(stop.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(200), elapsed);
        Assert.True(calls > 1);

        Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            elapsed += duration;
            clock.Advance(duration);
            if (++calls == 1)
            {
                control.Pause();
            }

            return Task.CompletedTask;
        }
    }

    // スケジューラーの実時間に依存せず、再開後の待機の未完了を確認します。
    private sealed class HeldSleepHost(ScriptPauseControl control) : IScriptHost
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DelayStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Output = [];

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Add(text);
            if (text == "1")
            {
                control.Pause();
                Started.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public async Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            DelayStarted.TrySetResult();
            await Release.Task.WaitAsync(token);
            await Task.Delay(duration, token);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref timestamp);

        internal void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }
}
