using System.Collections;
using System.Collections.Concurrent;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// fake sessionでTHREADの起動と終了の故障境界を検証します。
/// </summary>
// GUI・入力・COM・native優先度を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ThreadFailureBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PriorityFactoryOrInitializationFailureReleasesFork(bool initialize)
    {
        var expected = new InvalidOperationException(
            initialize ? "priority初期化失敗" : "priority生成失敗");
        var windows = new Automation
        {
            FactoryError = initialize ? null : expected,
            InitializeError = initialize ? expected : null
        };
        var host = new GateHost();
        Exception? error = await CaptureAsync("""
            THREAD Worker()
            PROCEDURE Worker()
                PRINT "worker-unreachable"
            FEND
            """, windows, host);
        Assert.NotNull(error);
        AssertContains(error, expected);
        Assert.DoesNotContain("worker-unreachable", host.Output);
        AssertReleased(windows);
        Assert.Equal(initialize ? 1 : 0, windows.PriorityDisposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainPrimaryRetainsWorkerCleanupFailure(bool hostFailure)
    {
        var cleanup = new IOException("worker解放失敗");
        var primary = new IOException("main出力失敗");
        var windows = new Automation { WorkerDisposeError = cleanup };
        var host = new GateHost { MainOutputError = hostFailure ? primary : null };
        Exception? error = await CaptureAsync("""
            OPTION OPTFINALLY
            THREAD Worker()
            TRY
                PRINT "main-entered"
                PRINT INT(NULL)
            FINALLY
                PRINT "main-cleanup"
            ENDTRY
            PROCEDURE Worker()
                SLEEP(0.02)
                SLEEP(30)
            FEND
            """, windows, host);
        Assert.NotNull(error);
        if (hostFailure)
        {
            Assert.Same(primary, error);
        }
        else
        {
            ScriptException script = Assert.IsType<ScriptException>(error);
            Assert.Equal(5, script.Line);
        }

        AssertContains(error, cleanup);
        Assert.Contains("main-cleanup", host.Output);
        AssertReleased(windows);
    }

    [Fact]
    public async Task WorkerExitWithCleanupFailureCannotReturnSuccessfulExitCode()
    {
        var cleanup = new IOException("EXITEXIT後のworker解放失敗");
        var windows = new Automation { WorkerDisposeError = cleanup };
        var host = new GateHost();
        Exception? error = await CaptureAsync(MainWithWorker("EXITEXIT 37"),
            windows, host);
        Assert.NotNull(error);
        AssertContains(error, cleanup);
        Assert.Contains("main-cleanup", host.Output);
        Assert.DoesNotContain("unreachable", host.Output);
        AssertReleased(windows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerFailureOrExitRunsMainFinally(bool exit)
    {
        var windows = new Automation();
        var host = new GateHost();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task<int> running = new Interpreter(windows: windows).RunWithExitCodeAsync(
            MainWithWorker(exit ? "EXITEXIT 37" : "PRINT 1/0"),
            host, cancellation.Token, ScriptRunOptions.Empty);
        if (exit)
        {
            Assert.Equal(37, await running.WaitAsync(TimeSpan.FromSeconds(12)));
        }
        else
        {
            await Assert.ThrowsAsync<ScriptException>(
                () => running.WaitAsync(TimeSpan.FromSeconds(12)));
        }

        Assert.Contains("main-entered", host.Output);
        Assert.Contains("main-cleanup", host.Output);
        Assert.DoesNotContain("unreachable", host.Output);
        AssertReleased(windows);
    }

    private static string MainWithWorker(string action)
        => """
            OPTION OPTFINALLY
            THREAD Worker()
            TRY
                PRINT "main-entered"
                SLEEP(30)
                PRINT "unreachable"
            FINALLY
                PRINT "main-cleanup"
            ENDTRY
            PROCEDURE Worker()
                SLEEP(0.02)
            """ + "\n" + action + "\nFEND\n";

    private static async Task<Exception?> CaptureAsync(string source,
        Automation windows, GateHost host)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        return await Record.ExceptionAsync(async () =>
            await new Interpreter(windows: windows).RunWithExitCodeAsync(
                source, host, cancellation.Token, ScriptRunOptions.Empty)
                .WaitAsync(TimeSpan.FromSeconds(12)));
    }

    private static void AssertReleased(Automation windows)
    {
        Assert.Equal(1, windows.Forks);
        Assert.Equal(0, windows.LiveForks);
        Assert.Equal(1, windows.WorkerDisposals);
        Assert.Equal(1, windows.RootDisposals);
        string[] events = [.. windows.Events];
        Assert.True(Array.IndexOf(events, "worker-dispose")
            < Array.IndexOf(events, "root-dispose"));
    }

    private static void AssertContains(Exception error, Exception expected)
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        Assert.True(Visit(error), "元の故障が例外または二次情報に保持されていません。");

        bool Visit(Exception current)
        {
            if (ReferenceEquals(current, expected))
            {
                return true;
            }

            if (!visited.Add(current))
            {
                return false;
            }

            if (current.InnerException is not null && Visit(current.InnerException))
            {
                return true;
            }

            if (current is AggregateException aggregate
                && aggregate.InnerExceptions.Any(Visit))
            {
                return true;
            }

            foreach (DictionaryEntry entry in current.Data)
            {
                if (entry.Value is Exception nested && Visit(nested))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class GateHost : IScriptHost
    {
        private readonly TaskCompletionSource mainEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Exception? MainOutputError
        {
            get; init;
        }

        internal ConcurrentQueue<string> Output { get; } = new();

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Enqueue(text);
            if (text == "main-entered")
            {
                mainEntered.TrySetResult();
                if (MainOutputError is not null)
                {
                    throw MainOutputError;
                }
            }

            return ValueTask.CompletedTask;
        }

        public async Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (duration == TimeSpan.FromMilliseconds(20))
            {
                // workerはmainのTRY本文に入るまで故障・終了しません。
                await mainEntered.Task.WaitAsync(token);
            }

            await Task.Delay(duration, token);
        }
    }

    private sealed class Automation : IWindowsAutomation
    {
        internal Exception? FactoryError
        {
            get; init;
        }

        internal Exception? InitializeError
        {
            get; init;
        }

        internal Exception? WorkerDisposeError
        {
            get; init;
        }

        internal ConcurrentQueue<string> Events { get; } = new();

        internal int Forks;
        internal int LiveForks;
        internal int WorkerDisposals;
        internal int RootDisposals;
        internal int PriorityDisposals;

        public IWindowsAutomationSession CreateSession() => new Session(this, true);

        private sealed class Session(Automation owner, bool root)
            : IWindowsAutomationSession
        {
            private int disposed;

            public IWindowsAutomationSession Fork()
            {
                Assert.True(root);
                Interlocked.Increment(ref owner.Forks);
                Interlocked.Increment(ref owner.LiveForks);
                owner.Events.Enqueue("fork");
                return new Session(owner, false);
            }

            public IWorkerThreadPriority CreateWorkerThreadPriority()
            {
                Assert.False(root);
                if (owner.FactoryError is not null)
                {
                    throw owner.FactoryError;
                }

                return new Priority(owner);
            }

            public ValueTask<Value> InvokeAsync(string name,
                IReadOnlyList<Value> arguments, CancellationToken token)
                => throw new InvalidOperationException("Windows操作は禁止です。");

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    if (root)
                    {
                        Interlocked.Increment(ref owner.RootDisposals);
                        owner.Events.Enqueue("root-dispose");
                        Assert.Equal(0, Volatile.Read(ref owner.LiveForks));
                    }
                    else
                    {
                        Interlocked.Decrement(ref owner.LiveForks);
                        Interlocked.Increment(ref owner.WorkerDisposals);
                        owner.Events.Enqueue("worker-dispose");
                    }
                }

                return !root && owner.WorkerDisposeError is not null
                    ? ValueTask.FromException(owner.WorkerDisposeError)
                    : ValueTask.CompletedTask;
            }
        }

        private sealed class Priority(Automation owner) : IWorkerThreadPriority
        {
            public void Initialize()
            {
                owner.Events.Enqueue("priority-initialize");
                if (owner.InitializeError is not null)
                {
                    throw owner.InitializeError;
                }
            }

            public void SetPriority(WorkerThreadPriority priority)
            {
                owner.Events.Enqueue("priority-set");
            }

            public void Dispose()
            {
                Interlocked.Increment(ref owner.PriorityDisposals);
                owner.Events.Enqueue("priority-dispose");
            }
        }
    }
}
