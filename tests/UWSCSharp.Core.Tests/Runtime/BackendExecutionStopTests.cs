using System.Collections.Concurrent;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class BackendExecutionStopTests
{
    [Fact]
    public async Task DefaultSessionKeepsTheCallerTokenUnchanged()
    {
        var adapter = new DefaultAdapter();
        using var caller = new CancellationTokenSource();
        await new Interpreter(windows: adapter).RunAsync(
            "PRINT GETID(\"owned\")", _ => { }, caller.Token);
        Assert.Equal(CancellationToken.None,
            ((IWindowsAutomationSession)adapter).ExecutionStopToken);
        Assert.Equal(caller.Token, adapter.ConfiguredToken);
        Assert.Equal(caller.Token, adapter.InvokedToken);
        Assert.True(adapter.Disposed);
    }

    [Fact]
    public async Task AlreadyStoppedBackendDoesNotStartTheScript()
    {
        using var adapter = new Adapter();
        adapter.Stop.Cancel();
        var output = new List<string>();
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter(windows: adapter).RunAsync("PRINT 1", output.Add));
        Assert.Equal(adapter.Stop.Token, failure.CancellationToken);
        Assert.Empty(output);
        Assert.Equal(1, adapter.RootDisposals);
    }

    [Fact]
    public async Task BackendStopBypassesExceptAndRunsFinally()
    {
        using var adapter = new Adapter();
        (Task run, ConcurrentQueue<string> output, Task ready) = Start(adapter,
            "OPTION OPTFINALLY\nTRY\n TRY\n PRINT \"ready\"\n SLEEP(99)\n EXCEPT\n PRINT \"caught\"\n ENDTRY\nFINALLY\n PRINT \"finally\"\nENDTRY\nPRINT \"after\"");
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Stop.Cancel();
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(adapter.Stop.Token, failure.CancellationToken);
            Assert.Equal(["ready", "finally"], [.. output]);
            Assert.Equal(1, adapter.RootDisposals);
        }
        finally
        {
            adapter.Stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task BackendStopWaitsForPendingOperationCleanup()
    {
        using var adapter = new Adapter { HoldInvoke = true };
        Task run = new Interpreter(windows: adapter).RunAsync("GETID(\"owned\")", _ => { });
        try
        {
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Stop.Cancel();
            await adapter.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(run.IsCompleted);
            Assert.Equal(0, adapter.RootDisposals);
            adapter.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(adapter.Cleaned);
            Assert.Equal(1, adapter.RootDisposals);
        }
        finally
        {
            adapter.Release.TrySetResult();
            adapter.Stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task BackendStopInterruptsPauseWithoutResumingIt()
    {
        using var adapter = new Adapter();
        using var pause = new ScriptPauseControl();
        pause.Pause();
        Task run = new Interpreter(windows: adapter).RunAsync("PRINT 1",
            new CallbackScriptHost(_ => { }), CancellationToken.None,
            new ScriptRunOptions([]) { PauseControl = pause });
        try
        {
            Assert.True(SpinWait.SpinUntil(() => pause.IsPaused, 3000));
            adapter.Stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(pause.IsPaused);
            Assert.True(pause.IsPauseRequested);
            Assert.Equal(1, adapter.RootDisposals);
        }
        finally
        {
            adapter.Stop.Cancel();
            pause.Resume();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task BackendStopCancelsRootAndWorkerAndWaitsForBoth()
    {
        using var adapter = new Adapter();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int outputs = 0;
        Task run = new Interpreter(windows: adapter).RunAsync(
            "THREAD Work()\nPRINT \"root\"\nSLEEP(99)\nPROCEDURE Work()\n PRINT \"worker\"\n SLEEP(99)\nFEND", text =>
            {
                if (Interlocked.Increment(ref outputs) == 2)
                {
                    ready.TrySetResult();
                }
            });
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, adapter.ChildDisposals);
            Assert.Equal(1, adapter.RootDisposals);
        }
        finally
        {
            adapter.Stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task CallerCancellationKeepsItsOriginalTokenWithBackendSupport()
    {
        using var adapter = new Adapter();
        using var caller = new CancellationTokenSource();
        (Task run, _, Task ready) = Start(adapter, "PRINT \"ready\"\nSLEEP(99)", caller.Token);
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotEqual(caller.Token, adapter.ConfiguredToken);
            caller.Cancel();
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(caller.Token, failure.CancellationToken);
        }
        finally
        {
            caller.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task BackendCancellationKeepsItsOriginAndCleanupDiagnostics()
    {
        using var adapter = new Adapter
        {
            HoldInvoke = true,
            DisposeFailure = new IOException("検証用のsession解放失敗")
        };
        using var caller = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync(
            "GETID(\"owned\")", _ => { }, caller.Token);
        try
        {
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Release.TrySetResult();
            adapter.Stop.Cancel();
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(adapter.Stop.Token, failure.CancellationToken);
            Assert.False(caller.IsCancellationRequested);
            Assert.IsType<TaskCanceledException>(failure);
            Assert.Same(adapter.LastCancellation, failure.InnerException);
            Assert.Contains(nameof(IWindowsAutomationSession.InvokeAsync),
                adapter.LastCancellation!.StackTrace);
            Assert.Equal("保持する診断", failure.Data["backend-stop-probe"]);
            AggregateException errors = Assert.IsType<AggregateException>(
                failure.Data["UWSCSharp.CleanupErrors"]);
            Assert.Contains(adapter.DisposeFailure, errors.InnerExceptions);
        }
        finally
        {
            adapter.Release.TrySetResult();
            adapter.Stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task IndependentBackendStopsDoNotLeakBetweenExecutions()
    {
        using var first = new Adapter();
        using var second = new Adapter();
        (Task firstRun, _, Task firstReady) = Start(first, "PRINT \"ready\"\nSLEEP(99)");
        (Task secondRun, _, Task secondReady) = Start(second, "PRINT \"ready\"\nSLEEP(99)");
        try
        {
            await Task.WhenAll(firstReady, secondReady).WaitAsync(TimeSpan.FromSeconds(3));
            first.Stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => firstRun.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(secondRun.IsCompleted);
            Assert.False(second.Stop.IsCancellationRequested);
            second.Stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => secondRun.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            first.Stop.Cancel();
            second.Stop.Cancel();
            await FinishAsync(firstRun);
            await FinishAsync(secondRun);
        }
    }

    [Fact]
    public async Task StopTokenGetterFailureStillDisposesTheCreatedSession()
    {
        using var adapter = new Adapter { GetterFailure = new ApplicationException("検証用のtoken取得失敗") };
        ApplicationException failure = await Assert.ThrowsAsync<ApplicationException>(
            () => new Interpreter(windows: adapter).RunAsync("PRINT 1", _ => { }));
        Assert.Same(adapter.GetterFailure, failure);
        Assert.Equal(1, adapter.RootDisposals);
    }

    [Fact]
    public async Task DefaultSessionStillReceivesCancellationDuringCreation()
    {
        using var caller = new CancellationTokenSource();
        var adapter = new DefaultAdapter { OnCreate = caller.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter(windows: adapter).RunAsync("PRINT 1", _ => { }, caller.Token));
        Assert.Equal(caller.Token, adapter.ConfiguredToken);
        Assert.True(adapter.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    public async Task StopDuringSessionCleanupDoesNotRewriteSelectedExitCode(int code)
    {
        using var adapter = new Adapter();
        adapter.OnDispose = adapter.Stop.Cancel;
        int result = await new Interpreter(windows: adapter).RunWithExitCodeAsync(
            $"EXITEXIT({code})", new CallbackScriptHost(_ => { }),
            CancellationToken.None, ScriptRunOptions.Empty);
        Assert.Equal(code, result);
        Assert.True(adapter.Stop.IsCancellationRequested);
    }

    [Fact]
    public async Task StopDuringSessionCleanupDoesNotReplaceThePrimaryFailure()
    {
        var error = new ApplicationException("検証用の実行エラー");
        using var adapter = new Adapter { InvokeFailure = error };
        adapter.OnDispose = adapter.Stop.Cancel;
        ApplicationException failure = await Assert.ThrowsAsync<ApplicationException>(
            () => new Interpreter(windows: adapter).RunAsync("GETID(\"owned\")", _ => { }));
        Assert.Same(error, failure);
        Assert.True(adapter.Stop.IsCancellationRequested);
    }

    private static (Task Run, ConcurrentQueue<string> Output, Task Ready) Start(
        Adapter adapter, string source, CancellationToken caller = default)
    {
        var output = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = new Interpreter(windows: adapter).RunAsync(source, text =>
        {
            output.Enqueue(text);
            if (text == "ready")
            {
                ready.TrySetResult();
            }
        }, caller);
        return (run, output, ready.Task);
    }

    private static async Task FinishAsync(Task run)
    {
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class DefaultAdapter : IWindowsAutomation, IWindowsAutomationSession
    {
        internal Action? OnCreate
        {
            get; init;
        }
        internal CancellationToken ConfiguredToken;
        internal CancellationToken InvokedToken;
        internal bool Disposed;

        public IWindowsAutomationSession CreateSession()
        {
            OnCreate?.Invoke();
            return this;
        }

        public void ConfigurePause(ScriptPauseControl? control, CancellationToken token) => ConfiguredToken = token;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken token)
        {
            InvokedToken = token;
            return ValueTask.FromResult(Value.Numeric(7));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Adapter : IWindowsAutomation, IDisposable
    {
        internal CancellationTokenSource Stop { get; } = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldInvoke
        {
            get; init;
        }
        internal Exception? GetterFailure
        {
            get; init;
        }
        internal Exception? DisposeFailure
        {
            get; init;
        }
        internal Exception? InvokeFailure
        {
            get; init;
        }
        internal Action? OnDispose
        {
            get; set;
        }
        internal CancellationToken ConfiguredToken;
        internal int RootDisposals;
        internal int ChildDisposals;
        internal bool Cleaned;
        internal OperationCanceledException? LastCancellation;

        public IWindowsAutomationSession CreateSession() => new Session(this, false);

        /// <summary>
        /// 偽の停止要求と待機を解消し、検証用の資源を破棄します。
        /// </summary>
        public void Dispose()
        {
            Stop.Cancel();
            Release.TrySetResult();
            Stop.Dispose();
        }

        private sealed class Session(Adapter owner, bool worker) : IWindowsAutomationSession
        {
            public CancellationToken ExecutionStopToken => owner.GetterFailure is { } error
                ? throw error : owner.Stop.Token;

            public IWindowsAutomationSession Fork() => new Session(owner, true);

            public void ConfigurePause(ScriptPauseControl? control, CancellationToken token)
            {
                if (!worker)
                {
                    owner.ConfiguredToken = token;
                }
            }

            public async ValueTask<Value> InvokeAsync(string name,
                IReadOnlyList<Value> arguments, CancellationToken token)
            {
                owner.Started.TrySetResult();
                if (owner.InvokeFailure is { } failure)
                {
                    throw failure;
                }

                if (owner.HoldInvoke)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException cancelled)
                    {
                        owner.LastCancellation = cancelled;
                        cancelled.Data["backend-stop-probe"] = "保持する診断";
                        throw;
                    }
                    finally
                    {
                        owner.Cancelled.TrySetResult();
                        await owner.Release.Task;
                        owner.Cleaned = true;
                    }
                }

                return Value.Numeric(7);
            }

            public ValueTask DisposeAsync()
            {
                if (worker)
                {
                    Interlocked.Increment(ref owner.ChildDisposals);
                }
                else
                {
                    Interlocked.Increment(ref owner.RootDisposals);
                    owner.OnDispose?.Invoke();
                    if (owner.DisposeFailure is { } failure)
                    {
                        throw failure;
                    }
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
