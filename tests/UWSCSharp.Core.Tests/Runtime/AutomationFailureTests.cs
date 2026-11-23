using System.ComponentModel;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AutomationFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultCancelsPendingOperationAndWaitsForCleanup(bool windowsWait)
    {
        var adapter = new Adapter();
        var host = new Host(adapter) { HoldCleanup = true };
        using var cancel = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync(
            windowsWait ? "GETID(\"owned\")" : "SLEEP(99)", host, cancel.Token);
        try
        {
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Fail();
            await adapter.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(run.IsCompleted);
            Assert.False(adapter.Disposed);
            adapter.Release.TrySetResult();
            ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() => run);
            Assert.Contains(BuiltinFunctionNames.LockHardEx, failure.Message);
            Assert.True(Contains(failure, adapter.Error));
            Assert.True(adapter.Cleaned);
            Assert.True(adapter.Disposed);
        }
        finally
        {
            adapter.Release.TrySetResult();
            cancel.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task HandledFaultDoesNotCancelSubsequentStatements()
    {
        var adapter = new Adapter();
        var host = new Host(adapter);
        using var cancel = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync(
            "TRY\n SLEEP(99)\nEXCEPT\n PRINT \"caught\"\nENDTRY\nPRINT \"alive\"", host, cancel.Token);
        try
        {
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Fail();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(["caught", "alive"], host.Output);
            Assert.False(cancel.IsCancellationRequested);
            Assert.True(adapter.Cleaned);
        }
        finally
        {
            cancel.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task PendingFaultPreventsStartingNewOperation()
    {
        var adapter = new Adapter();
        adapter.Fail();
        using var cancel = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync("SLEEP(99)", new Host(adapter), cancel.Token);
        try
        {
            ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(Contains(failure, adapter.Error));
            Assert.False(adapter.Started.Task.IsCompleted);
            Assert.True(adapter.Disposed);
        }
        finally
        {
            cancel.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task FaultIsObservedWhenOperationCompletesSynchronously()
    {
        var adapter = new Adapter { FailWhenCompleting = true };
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter(windows: adapter)
            .RunAsync("GETID(\"owned\")", new Host(adapter)));
        Assert.True(adapter.Disposed);
    }

    [Fact]
    public async Task FaultInterruptsPauseWithoutResumingIt()
    {
        var adapter = new Adapter();
        using var pause = new ScriptPauseControl();
        using var cancel = new CancellationTokenSource();
        pause.Pause();
        Task run = new Interpreter(windows: adapter).RunAsync("PRINT 1", new Host(adapter),
            cancel.Token, new ScriptRunOptions([]) { PauseControl = pause });
        try
        {
            Assert.True(SpinWait.SpinUntil(() => pause.IsPaused, 3000));
            adapter.Fail();
            await Assert.ThrowsAsync<ScriptException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(pause.IsPaused);
            Assert.True(pause.IsPauseRequested);
            Assert.True(adapter.Disposed);
        }
        finally
        {
            cancel.Cancel();
            pause.Resume();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task ExternalCancellationPreservesItsToken()
    {
        var adapter = new Adapter();
        using var cancel = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync("SLEEP(99)", new Host(adapter), cancel.Token);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.True(adapter.Cleaned);
        Assert.True(adapter.Disposed);
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplaceNativeFault()
    {
        var adapter = new Adapter { CleanupError = new ApplicationException("操作回収の失敗") };
        using var stop = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync("SLEEP(99)", new Host(adapter), stop.Token);
        try
        {
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Fail();
            ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Same(adapter.Error, failure.InnerException);
            AggregateException cleanup = Assert.IsType<AggregateException>(failure.Data["UWSCSharp.AutomationCleanupErrors"]);
            Assert.Contains(adapter.CleanupError, cleanup.Flatten().InnerExceptions);
            Assert.True(adapter.Cleaned);
        }
        finally
        {
            stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task CancellationCallbackFailureIsRetainedAfterOperationCleanup()
    {
        var adapter = new Adapter
        {
            CallbackError = new ApplicationException("取消通知の失敗"),
            HoldCleanup = true
        };
        using var stop = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync("SLEEP(99)", new Host(adapter), stop.Token);
        try
        {
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Fail();
            await adapter.CallbackInvoked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Release.TrySetResult();
            ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Same(adapter.Error, failure.InnerException);
            AggregateException cleanup = Assert.IsType<AggregateException>(failure.Data["UWSCSharp.AutomationCleanupErrors"]);
            Assert.Contains(adapter.CallbackError, cleanup.Flatten().InnerExceptions);
            Assert.True(adapter.Cleaned);
        }
        finally
        {
            adapter.Release.TrySetResult();
            stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task CancellationDuringFailureReadPreservesCancellationAndNativeError()
    {
        var adapter = new Adapter();
        using var stop = new CancellationTokenSource();
        adapter.OnTakeFailure = stop.Cancel;
        Task run = new Interpreter(windows: adapter).RunAsync("SLEEP(99)", new Host(adapter), stop.Token);
        try
        {
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Fail();
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(stop.Token, failure.CancellationToken);
            Assert.True(ContainsStored(failure, adapter.Error));
            Assert.True(adapter.Cleaned);
        }
        finally
        {
            stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCancellationDiagnosticsDoNotReferenceEachOther(bool hasData)
    {
        var adapter = new Adapter();
        using var stop = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        var operationError = new OperationCanceledException(other.Token);
        if (hasData)
        {
            operationError.Data["診断"] = "操作の取消";
        }

        adapter.CleanupError = operationError;
        adapter.OnTakeFailure = stop.Cancel;
        Task run = new Interpreter(windows: adapter).RunAsync(
            "SLEEP(99)", new Host(adapter), stop.Token);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        adapter.Fail();
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(HasCycle(failure, new HashSet<Exception>(ReferenceEqualityComparer.Instance)));
        Assert.Equal(stop.Token, failure.CancellationToken);
        Assert.True(ContainsStored(failure, adapter.Error));
        Assert.True(ContainsStored(failure, operationError));
        Assert.True(adapter.Cleaned);
    }

    [Fact]
    public async Task SynchronousOperationFailureKeepsConcurrentNativeError()
    {
        var adapter = new Adapter
        {
            FailWhenCompleting = true,
            CompletionError = new ApplicationException("操作の失敗")
        };
        ApplicationException failure = await Assert.ThrowsAsync<ApplicationException>(() =>
            new Interpreter(windows: adapter).RunAsync("GETID(\"owned\")", new Host(adapter)));
        Assert.Same(adapter.CompletionError, failure);
        Assert.True(ContainsStored(failure, adapter.Error));
        Assert.True(adapter.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultStopsWaitingRootAndWorkerWithoutLeavingOperations(bool worker)
    {
        var adapter = new Adapter { FailureOnWorker = worker };
        using var stop = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync(
            "THREAD Work()\nSLEEP(99)\nPROCEDURE Work()\n SLEEP(99)\nFEND", new Host(adapter), stop.Token);
        try
        {
            await adapter.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Fail();
            ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(Contains(failure, adapter.Error));
            Assert.Equal(2, adapter.CleanedCount);
            Assert.Equal(1, adapter.ChildDisposals);
            Assert.True(adapter.Disposed);
        }
        finally
        {
            stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task FailureReaderErrorCancelsAndRecoversPendingOperation()
    {
        var adapter = new Adapter { ReaderError = new ApplicationException("障害読取りの失敗") };
        using var stop = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync("SLEEP(99)", new Host(adapter), stop.Token);
        try
        {
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await adapter.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            ApplicationException failure = await Assert.ThrowsAsync<ApplicationException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Same(adapter.ReaderError, failure);
            Assert.True(adapter.Cleaned);
            Assert.True(adapter.Disposed);
        }
        finally
        {
            adapter.Abort.TrySetResult();
            stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Theory]
    [InlineData("o.Call()")]
    [InlineData("PRINT o.Value")]
    [InlineData("o.Value=1")]
    [InlineData("o.Change(VAR x)")]
    public async Task MemberFaultCancelsOperationAndAllowsTryContinuation(string member)
    {
        var adapter = new Adapter();
        var host = new Host(adapter);
        using var stop = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync(
            "DIM o=CREATEOLEOBJ(\"fixture\")\nDIM x=1\nTRY\n " + member
            + "\nEXCEPT\n PRINT \"caught\"\nENDTRY\nPRINT \"alive\"", host, stop.Token);
        try
        {
            await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            adapter.Fail();
            await adapter.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(["caught", "alive"], host.Output);
            Assert.True(adapter.Cleaned);
            Assert.False(adapter.MemberEffectApplied);
            Assert.False(stop.IsCancellationRequested);
        }
        finally
        {
            adapter.Abort.TrySetResult();
            stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Theory]
    [InlineData("PUBLIC x=GETID(\"owned\")", 1)]
    [InlineData("MODULE Box\n DIM x=GETID(\"owned\")\nENDMODULE", 2)]
    public async Task InitializerFaultKeepsItsScriptLocation(string source, int line)
    {
        var adapter = new Adapter();
        adapter.Fail();
        using var stop = new CancellationTokenSource();
        Task run = new Interpreter(windows: adapter).RunAsync(source, new Host(adapter), stop.Token);
        try
        {
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(line, error.Line);
            Assert.True(Contains(error, adapter.Error));
            Assert.False(adapter.Started.Task.IsCompleted);
        }
        finally
        {
            adapter.Abort.TrySetResult();
            stop.Cancel();
            await FinishAsync(run);
        }
    }

    [Fact]
    public async Task SameOperationAndNotificationErrorDoesNotReferenceItselfThroughData()
    {
        var adapter = new Adapter { FailWhenCompleting = true };
        adapter.CompletionError = adapter.Error;
        Win32Exception failure = await Assert.ThrowsAsync<Win32Exception>(() =>
            new Interpreter(windows: adapter).RunAsync("GETID(\"owned\")", new Host(adapter)));
        Assert.Same(adapter.Error, failure);
        Assert.False(HasCycle(failure, new HashSet<Exception>(ReferenceEqualityComparer.Instance)));
        Assert.Contains(failure.Data.Values.Cast<object>().OfType<IEnumerable<string>>()
            .SelectMany(names => names), name => name == BuiltinFunctionNames.LockHardEx);
    }

    private static bool HasCycle(Exception error, HashSet<Exception> ancestors)
    {
        if (!ancestors.Add(error))
        {
            return true;
        }

        bool found = error.InnerException is { } inner && HasCycle(inner, ancestors);
        if (!found && error is AggregateException aggregate)
        {
            found = aggregate.InnerExceptions.Any(item => HasCycle(item, ancestors));
        }

        found |= error.Data.Values.Cast<object>().OfType<Exception>()
            .Any(item => HasCycle(item, ancestors));
        ancestors.Remove(error);
        return found;
    }

    private static bool ContainsStored(Exception error, Exception expected)
    {
        if (Contains(error, expected))
        {
            return true;
        }

        if (error is AggregateException aggregate
            && aggregate.InnerExceptions.Any(item => ContainsStored(item, expected)))
        {
            return true;
        }

        return error.Data.Values.Cast<object>().OfType<Exception>()
            .Any(item => ContainsStored(item, expected));
    }

    private static bool Contains(Exception error, Exception expected)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (ReferenceEquals(current, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task FinishAsync(Task run)
    {
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 本体の検査結果を保ち、偽操作と実行を後始末します。
        }
    }

    // 画面・入力・OSフックを使わず、取消と後始末の順序だけを検査します。
    private sealed class Adapter : IWindowsAutomation, IWindowsAutomationSession
    {
        private WindowsAutomationFailure? pending;
        private int startedCount;
        internal readonly Win32Exception Error = new(5, "フック障害");
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource BothStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Abort = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CallbackInvoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        internal bool Cleaned;
        internal bool HoldCleanup;
        internal bool FailWhenCompleting;
        internal Exception? CleanupError;
        internal Exception? CallbackError;
        internal Exception? CompletionError;
        internal Exception? ReaderError;
        internal Action? OnTakeFailure;
        internal int CleanedCount;
        internal int ChildDisposals;
        internal bool? FailureOnWorker;
        internal bool MemberEffectApplied;

        public bool RequiresAutomationFailurePolling => true;

        public IWindowsAutomationSession CreateSession() => this;

        public WindowsAutomationFailure? TakeAutomationFailure() => ReadFailure(false);

        internal WindowsAutomationFailure? ReadFailure(bool worker)
        {
            if (FailureOnWorker is { } selected && selected != worker)
            {
                return null;
            }

            if (ReaderError is not null && Volatile.Read(ref startedCount) > 0)
            {
                throw ReaderError;
            }

            WindowsAutomationFailure? failure = Interlocked.Exchange(ref pending, null);
            if (failure is not null)
            {
                OnTakeFailure?.Invoke();
            }

            return failure;
        }

        public IWindowsAutomationSession Fork() => new Child(this);

        internal void Fail() => Interlocked.Exchange(ref pending, new(BuiltinFunctionNames.LockHardEx, Error));

        public async ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken)
        {
            if (name == BuiltinFunctionNames.CreateOleObj)
            {
                return Value.FromObject(this);
            }

            if (FailWhenCompleting)
            {
                Fail();
                if (CompletionError is not null)
                {
                    throw CompletionError;
                }

                return Value.Numeric(7);
            }

            await WaitAsync(cancellationToken);
            return Value.Numeric(7);
        }

        public async ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken cancellationToken)
        {
            await WaitAsync(cancellationToken);
            MemberEffectApplied = true;
            return Value.Numeric(7);
        }

        public async ValueTask<WindowsMemberResult> InvokeMemberWithReferencesAsync(Value target,
            string name, IReadOnlyList<WindowsMemberArgument> arguments,
            Value? assigned, CancellationToken cancellationToken)
        {
            await WaitAsync(cancellationToken);
            MemberEffectApplied = true;
            return new(Value.Numeric(7), [.. arguments.Select(argument => argument.Value)]);
        }

        internal async Task WaitAsync(CancellationToken token)
        {
            using CancellationTokenRegistration registration = CallbackError is null
                ? default : token.Register(() =>
                {
                    CallbackInvoked.TrySetResult();
                    throw CallbackError;
                });
            if (Interlocked.Increment(ref startedCount) == 2)
            {
                BothStarted.TrySetResult();
            }

            Started.TrySetResult();
            try
            {
                await Abort.Task.WaitAsync(token);
            }
            finally
            {
                Cancelled.TrySetResult();
                if (HoldCleanup)
                {
                    await Release.Task;
                }

                Cleaned = true;
                Interlocked.Increment(ref CleanedCount);
                if (CleanupError is not null)
                {
                    throw CleanupError;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Child(Adapter owner) : IWindowsAutomationSession
    {
        public bool RequiresAutomationFailurePolling => true;

        public WindowsAutomationFailure? TakeAutomationFailure() => owner.ReadFailure(true);

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken) => owner.InvokeAsync(name, arguments, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner.ChildDisposals);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Host(Adapter adapter) : IScriptHost
    {
        internal readonly List<string> Output = [];

        internal bool HoldCleanup
        {
            init => adapter.HoldCleanup = value;
        }

        public ValueTask WriteOutputAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
            adapter.WaitAsync(cancellationToken);
    }
}
