using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class PeriodicLogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-periodic-" + Guid.NewGuid());

    public PeriodicLogTests()
    {
        Directory.CreateDirectory(directory);
    }

    private string ScriptPath => Path.Combine(directory, "main.uws");

    private string LogPath => Path.Combine(directory, "UWSC.LOG");

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task TickSavesWhileRunningAndShutdownFlushesRemainingOutput()
    {
        var clock = new ManualClock();
        await using var running = new RunningLog(ScriptPath, clock, _ => Assert.Fail());
        IScriptHost host = await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(LogPath));
        clock.Tick();
        await WaitForFileAsync(LogPath);
        Assert.False(running.Execution.IsCompleted);
        Assert.Equal("one\r\n", await ReadSharedAsync(LogPath));
        await host.WriteOutputAsync("two", default);
        Assert.Equal(7, await running.FinishAsync());
        Assert.Equal("one\r\ntwo\r\n", await ReadSharedAsync(LogPath));
        Assert.True(clock.IsDisposed);
    }

    [Fact]
    public async Task FailureIsNotifiedAndNextTickRetriesPendingOutput()
    {
        Directory.CreateDirectory(LogPath);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int notifications = 0;
        var clock = new ManualClock();
        await using var running = new RunningLog(ScriptPath, clock, _ =>
        {
            Interlocked.Increment(ref notifications);
            reported.TrySetResult();
        });
        await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Tick();
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(running.Execution.IsCompleted);
        Directory.Delete(LogPath);
        clock.Tick();
        await WaitForFileAsync(LogPath);
        Assert.Equal(7, await running.FinishAsync());
        Assert.Equal(1, notifications);
        Assert.Equal("one\r\n", await ReadSharedAsync(LogPath));
    }

    [Fact]
    public async Task ShutdownWaitsForNotifierBeforeFinalSave()
    {
        Directory.CreateDirectory(LogPath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var clock = new ManualClock();
        await using var running = new RunningLog(ScriptPath, clock, _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var tick = Task.Run(clock.Tick);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            running.Complete.TrySetResult();
            await clock.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.Execution.IsCompleted);
            Directory.Delete(LogPath);
        }
        finally
        {
            release.Set();
            await tick.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(7, await running.FinishAsync());
        Assert.Equal("one\r\n", await ReadSharedAsync(LogPath));
    }

    [Fact]
    public async Task TimerCreationFailureDoesNotExecuteScript()
    {
        var failure = new IOException("timer create");
        var clock = new ManualClock { CreationFailure = failure };
        bool executed = false;
        IOException observed = await Assert.ThrowsAsync<IOException>(() =>
            ScriptLogHost.RunScriptAsync(new CallbackScriptHost(_ => { }), ScriptPath,
                _ =>
                {
                    executed = true;
                    return Task.FromResult(0);
                }, _ => { }, clock));
        Assert.Same(failure, observed);
        Assert.False(executed);
        Assert.False(File.Exists(LogPath));
        // 生成失敗したオブジェクトの回収後も、プロセスが停止しないことを確認します。
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    [Fact]
    public async Task AsyncTimerDisposalCompletesBeforeFinalSave()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualClock { DisposalRelease = release };
        await using var running = new RunningLog(ScriptPath, clock, _ => Assert.Fail());
        await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        running.Complete.TrySetResult();
        try
        {
            await clock.AsyncDisposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.Execution.IsCompleted);
            Assert.False(File.Exists(LogPath));
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(7, await running.FinishAsync());
        Assert.Equal("one\r\n", await ReadSharedAsync(LogPath));
    }

    [Fact]
    public async Task TimerDisposeFailureDoesNotReplacePrimaryAndStillFlushes()
    {
        var failure = new IOException("timer dispose");
        var primary = new InvalidOperationException("primary");
        var clock = new ManualClock { DisposalFailure = failure };
        await using var running = new RunningLog(ScriptPath, clock, _ => { }, primary);
        await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(
            running.FinishAsync);
        Assert.Same(primary, observed);
        AggregateException cleanup = Assert.IsType<AggregateException>(
            observed.Data["UWSCSharp.CleanupErrors"]);
        Assert.Same(failure, Assert.Single(cleanup.InnerExceptions));
        Assert.True(clock.IsDisposed);
        Assert.Equal("one\r\n", await ReadSharedAsync(LogPath));
    }

    [Fact]
    public async Task NotifierFailureIsRetainedWithPrimaryAndFinalSaveStillRuns()
    {
        Directory.CreateDirectory(LogPath);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationFailure = new InvalidOperationException("notification");
        var primary = new IOException("primary");
        var clock = new ManualClock();
        await using var running = new RunningLog(ScriptPath, clock, _ =>
        {
            notified.TrySetResult();
            throw notificationFailure;
        }, primary);
        await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Tick();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Directory.Delete(LogPath);
        IOException observed = await Assert.ThrowsAsync<IOException>(running.FinishAsync);
        Assert.Same(primary, observed);
        AggregateException cleanup = Assert.IsType<AggregateException>(
            observed.Data["UWSCSharp.CleanupErrors"]);
        Assert.Contains(notificationFailure, cleanup.Flatten().InnerExceptions);
        Assert.Equal("one\r\n", await ReadSharedAsync(LogPath));
    }

    [Fact]
    public async Task NotifierFailureCannotBeHiddenByNormalCompletionAndSuccessfulFinalSave()
    {
        Directory.CreateDirectory(LogPath);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationFailure = new InvalidOperationException("notification");
        var clock = new ManualClock();
        await using var running = new RunningLog(ScriptPath, clock, _ =>
        {
            notified.TrySetResult();
            throw notificationFailure;
        });
        await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Tick();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Directory.Delete(LogPath);
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(running.FinishAsync);
        Assert.Contains(notificationFailure, error.Flatten().InnerExceptions);
        Assert.True(clock.IsDisposed);
        Assert.Equal("one\r\n", await ReadSharedAsync(LogPath));
    }

    [Fact]
    public async Task UnrecoveredPeriodicSaveFailureIsReturnedByFinalSave()
    {
        Directory.CreateDirectory(LogPath);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualClock();
        await using var running = new RunningLog(ScriptPath, clock,
            _ => notified.TrySetResult());
        await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Tick();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Exception? failure = await Record.ExceptionAsync(running.FinishAsync);
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.True(clock.IsDisposed);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task ExistingEndOnlyOverloadDoesNotCreateTimer()
    {
        var clock = new ManualClock { CreationFailure = new IOException("not reached") };
        int code = await ScriptLogHost.RunAsync(new CallbackScriptHost(_ => { }), LogPath,
            async host =>
            {
                await host.WriteOutputAsync("legacy", default);
                return 9;
            }, mode: ScriptLogMode.TextOnly, timeProvider: clock);
        Assert.Equal(9, code);
        Assert.False(clock.Created);
        Assert.Equal("legacy\r\n", await ReadSharedAsync(LogPath));
    }

    [Fact]
    public async Task ConfigurationAndPeriodicSaveDoNotDuplicateOrMoveEarlierOutput()
    {
        var clock = new ManualClock();
        await using var running = new RunningLog(ScriptPath, clock, _ => Assert.Fail());
        IScriptHost output = await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Tick();
        await WaitForFileAsync(LogPath);
        IScriptLogHost host = Assert.IsAssignableFrom<IScriptLogHost>(output);
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: "second.log"), default);
        await host.WriteOutputAsync("two", default);
        clock.Tick();
        string second = Path.Combine(directory, "second.log");
        await WaitForFileAsync(second);
        Assert.Equal(7, await running.FinishAsync());
        Assert.Equal("one\r\n", await ReadSharedAsync(LogPath));
        Assert.Equal("two\r\n", await ReadSharedAsync(second));
    }

    [Fact]
    public async Task CancellationKeepsTokenAndStopsTimerAfterSavedPrint()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new DelayedHost();
        var clock = new ManualClock();
        Task<int> execution = ScriptLogHost.RunScriptAsync(output, ScriptPath,
            host => new Interpreter().RunWithExitCodeAsync("""
                OPTION LOGFILE=2
                PRINT "one"
                SLEEP(60)
                """, host, cancellation.Token, ScriptRunOptions.Empty),
            _ => Assert.Fail(), clock);
        try
        {
            await output.Delaying.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Tick();
            await WaitForFileAsync(LogPath);
        }
        finally
        {
            cancellation.Cancel();
        }

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(clock.IsDisposed);
        Assert.Equal("one\r\n", await ReadSharedAsync(LogPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DisabledSavingDoesNotCreateFileOnTick(int choice)
    {
        var clock = new ManualClock();
        await using var running = new RunningLog(ScriptPath, clock, _ => Assert.Fail());
        IScriptLogHost host = Assert.IsAssignableFrom<IScriptLogHost>(
            await running.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        // 初期出力は旧設定で保存されるため、変更後の新しいパスを確認します。
        string disabled = Path.Combine(directory, "disabled.log");
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: disabled,
            MaximumLines: choice == 0 ? 0 : null, FileMode: choice == 1 ? 1 : null), default);
        await host.WriteOutputAsync("not saved", default);
        clock.Tick();
        Assert.Equal(7, await running.FinishAsync());
        Assert.False(File.Exists(disabled));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.1)]
    [InlineData(4_294_967_295d)]
    public async Task InvalidPeriodDoesNotStartTimerOrScript(double milliseconds)
    {
        var clock = new ManualClock();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ScriptLogHost.RunScriptAsync(new CallbackScriptHost(_ => { }), ScriptPath,
                _ => throw new InvalidOperationException("not reached"), _ => { }, clock,
                TimeSpan.FromMilliseconds(milliseconds)));
        Assert.False(clock.Created);
        Assert.False(File.Exists(LogPath));
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private sealed class RunningLog : IAsyncDisposable
    {
        internal TaskCompletionSource<IScriptHost> Ready
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Complete
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<int> Execution
        {
            get;
        }

        internal RunningLog(string scriptPath, ManualClock clock,
            Action<Exception> reporter, Exception? primary = null)
        {
            Execution = ScriptLogHost.RunScriptAsync(new CallbackScriptHost(_ => { }),
                scriptPath, async host =>
                {
                    await ((IScriptLogHost)host).ConfigureLogAsync(
                        new ScriptLogSettings(FileMode: 2), default);
                    await host.WriteOutputAsync("one", default);
                    Ready.TrySetResult(host);
                    await Complete.Task;
                    if (primary is not null)
                    {
                        throw primary;
                    }

                    return 7;
                }, reporter, clock);
        }

        internal Task<int> FinishAsync()
        {
            Complete.TrySetResult();
            return Execution.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            Complete.TrySetResult();
            try
            {
                await Execution.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // 実行例外は各テストで検査し、後片付けで再送しません。
            }
        }
    }

    private sealed class DelayedHost : IScriptHost
    {
        internal TaskCompletionSource Delaying
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteOutputAsync(string text, CancellationToken token) =>
            ValueTask.CompletedTask;

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            Delaying.TrySetResult();
            return Task.Delay(duration, token);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private ManualTimer? timer;

        internal TaskCompletionSource Disposed
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AsyncDisposing
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource? DisposalRelease
        {
            get; init;
        }

        internal Exception? CreationFailure
        {
            get; init;
        }

        internal Exception? DisposalFailure
        {
            get; init;
        }

        internal bool IsDisposed => Disposed.Task.IsCompleted;

        internal bool Created => timer is not null;

        internal void Tick() => timer!.Tick();

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            if (CreationFailure is not null)
            {
                throw CreationFailure;
            }

            timer = new ManualTimer(this, callback, state);
            return timer;
        }

        private sealed class ManualTimer(ManualClock owner,
            TimerCallback callback, object? state) : ITimer
        {
            private int disposed;

            internal void Tick()
            {
                if (Volatile.Read(ref disposed) == 0)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                Volatile.Read(ref disposed) == 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    owner.Disposed.TrySetResult();
                    if (owner.DisposalFailure is not null)
                    {
                        throw owner.DisposalFailure;
                    }
                }
            }

            public async ValueTask DisposeAsync()
            {
                Dispose();
                owner.AsyncDisposing.TrySetResult();
                if (owner.DisposalRelease is not null)
                {
                    await owner.DisposalRelease.Task;
                }
            }
        }
    }
}
