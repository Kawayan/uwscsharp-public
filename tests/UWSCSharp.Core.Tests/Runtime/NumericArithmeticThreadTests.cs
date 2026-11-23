using System.Collections.Concurrent;
using Xunit;

namespace UWSCSharp.Core.Tests;

// 偽の演算部品と協調待機だけを使い、GUI・入力・nativeを操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NumericArithmeticThreadTests
{
    [Fact]
    public async Task WorkerAndNestedWorkerOwnDistinctScopesIncludingConditionsAndForUpdates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        TaskCompletionSource parent = Signal();
        TaskCompletionSource child = Signal();
        var host = new Host(async (duration, token) =>
        {
            if (duration.TotalSeconds == 1)
            {
                await Task.WhenAll(parent.Task, child.Task).WaitAsync(token);
            }
            else if (duration.TotalSeconds == 2)
            {
                parent.TrySetResult();
                await child.Task.WaitAsync(token);
            }
            else
            {
                child.TrySetResult();
            }
        });
        var factory = new Arithmetic();
        await Run("""
            THREAD Parent(7+2)
            SLEEP(1)
            PRINT 7+2
            PROCEDURE Parent(argument)
                PRINT argument
                PRINT 7+2
                IFB 7+2=101
                    PRINT "condition"
                ENDIF
                FOR i=1 TO 1
                    PRINT i
                NEXT
                THREAD Child()
                SLEEP(2)
            FEND
            PROCEDURE Child()
                PRINT 7+2
                SLEEP(3)
            FEND
            """, factory, host, timeout.Token);
        Assert.Equal(2, factory.RootCalls);
        Scope[] scopes = [.. factory.Scopes];
        Assert.Equal(2, scopes.Length);
        Assert.Equal(3, scopes[0].Calls);
        Assert.Equal(1, scopes[1].Calls);
        Assert.All(scopes, scope => Assert.Equal(1, scope.Disposals));
        Assert.Equal(0, factory.Disposals);
        Assert.Equal(["1", "101", "102", "9", "9", "condition"],
            host.Output.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationKeepsTheScopeThroughFinallyAndReportsCleanupFailure(bool failCleanup)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        TaskCompletionSource entered = Signal();
        var cleanup = new InvalidOperationException("演算主体の解放失敗");
        var factory = new Arithmetic { CleanupFailure = failCleanup ? cleanup : null };
        var host = new Host((duration, token) =>
        {
            if (duration.TotalSeconds == 2)
            {
                entered.TrySetResult();
            }

            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        Task run = Run("""
            OPTION OPTFINALLY
            THREAD Worker()
            SLEEP(1)
            PROCEDURE Worker()
                TRY
                    SLEEP(2)
                FINALLY
                    PRINT 7+2
                ENDTRY
            FEND
            """, factory, host, cancellation.Token);
        await entered.Task.WaitAsync(timeout.Token);
        await cancellation.CancelAsync();
        Exception? failure = await Record.ExceptionAsync(() => run);
        Assert.NotNull(failure);
        if (failCleanup)
        {
            AssertRetained(failure, cleanup);
        }

        Assert.Equal(["101"], host.Output);
        Assert.Equal(1, Assert.Single(factory.Scopes).Disposals);
        Assert.Equal(0, factory.Disposals);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerFailureAndCleanupFailureAreBothRetained(bool failEvaluation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var primary = new InvalidOperationException("worker演算の失敗");
        var cleanup = new InvalidOperationException("worker演算解放の失敗");
        var factory = new Arithmetic
        {
            EvaluationFailure = failEvaluation ? primary : null,
            CleanupFailure = cleanup
        };
        var host = new Host((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        Exception? failure = await Record.ExceptionAsync(() => Run("""
            THREAD Worker()
            SLEEP(1)
            PROCEDURE Worker()
                PRINT 7+2
            FEND
            """, factory, host, timeout.Token));
        Assert.NotNull(failure);
        AssertRetained(failure, cleanup);
        if (failEvaluation)
        {
            AssertRetained(failure, primary);
        }

        Assert.Equal(1, Assert.Single(factory.Scopes).Disposals);
        Assert.Equal(0, factory.Disposals);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task FailedScopeCreationSkipsTheBodyAndPreservesBorrowedOwnership()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var expected = new InvalidOperationException("演算主体の生成失敗");
        var factory = new Arithmetic { ForkFailure = expected };
        var host = new Host((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        Exception? failure = await Record.ExceptionAsync(() => Run("""
            THREAD Worker()
            SLEEP(1)
            PROCEDURE Worker()
                PRINT "unreachable"
            FEND
            """, factory, host, timeout.Token));
        Assert.NotNull(failure);
        AssertRetained(failure, expected);
        Assert.Empty(host.Output);
        Assert.Empty(factory.Scopes);
        Assert.Equal(0, factory.Disposals);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task NonforkingBackendRemainsBorrowedAndShared()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        TaskCompletionSource completed = Signal();
        var host = new Host((duration, token) =>
        {
            if (duration.TotalSeconds == 2)
            {
                completed.TrySetResult();
                return Task.CompletedTask;
            }

            return completed.Task.WaitAsync(token);
        });
        var backend = new Scope(42, null, null);
        await Run("""
            THREAD Worker()
            SLEEP(1)
            PRINT 7+2
            PROCEDURE Worker()
                PRINT 7+2
                SLEEP(2)
            FEND
            """, backend, host, timeout.Token);
        Assert.Equal(["42", "42"], host.Output);
        Assert.Equal(2, backend.Calls);
        Assert.Equal(0, backend.Disposals);
    }

    [Fact]
    public async Task PauseConfigurationFailureStillReleasesBothWorkerResources()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var primary = new InvalidOperationException("workerの一時停止接続失敗");
        var windowsCleanup = new InvalidOperationException("Windows主体の解放失敗");
        var numericCleanup = new InvalidOperationException("演算主体の解放失敗");
        var windows = new FailingPauseAutomation(primary, windowsCleanup);
        var host = new Host((_, token) => windows.Attempted.Task.WaitAsync(token));
        var factory = new Arithmetic { CleanupFailure = numericCleanup };
        Exception? failure = await Record.ExceptionAsync(() =>
            new Interpreter(windows: windows).RunAsync("""
                THREAD Worker()
                SLEEP(1)
                PROCEDURE Worker()
                    PRINT "unreachable"
                FEND
                """, host, timeout.Token,
                new ScriptRunOptions([]) { NumericArithmetic = factory }));
        Assert.NotNull(failure);
        AssertRetained(failure, primary);
        AssertRetained(failure, windowsCleanup);
        AssertRetained(failure, numericCleanup);
        Assert.Empty(host.Output);
        Assert.Equal(1, windows.WorkerDisposals);
        Assert.Equal(1, windows.RootDisposals);
        Assert.Equal(1, Assert.Single(factory.Scopes).Disposals);
        Assert.Equal(0, factory.Disposals);
        Assert.False(timeout.IsCancellationRequested);
    }

    private static Task Run(string source, INumericArithmetic arithmetic,
        IScriptHost host, CancellationToken token) => new Interpreter().RunAsync(
            source, host, token, new ScriptRunOptions([]) { NumericArithmetic = arithmetic });

    private static TaskCompletionSource Signal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertRetained(Exception actual, Exception expected)
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        Assert.True(Contains(actual));

        bool Contains(Exception current)
        {
            if (ReferenceEquals(current, expected))
            {
                return true;
            }

            if (!visited.Add(current))
            {
                return false;
            }

            return current.InnerException is not null && Contains(current.InnerException)
                || current is AggregateException aggregate && aggregate.InnerExceptions.Any(Contains)
                || current.Data.Values.Cast<object>().OfType<Exception>().Any(Contains);
        }
    }

    private sealed class Arithmetic : IForkableNumericArithmetic, IDisposable
    {
        internal Exception? ForkFailure;
        internal Exception? EvaluationFailure;
        internal Exception? CleanupFailure;
        internal int RootCalls;
        internal int Disposals;
        private int forks;
        internal ConcurrentQueue<Scope> Scopes { get; } = new();

        public INumericArithmeticScope ForkForThread()
        {
            if (ForkFailure is not null)
            {
                throw ForkFailure;
            }

            var scope = new Scope(100 + Interlocked.Increment(ref forks),
                EvaluationFailure, CleanupFailure);
            Scopes.Enqueue(scope);
            return scope;
        }

        public double Evaluate(NumericBinaryOperation operation, double left, double right)
        {
            Interlocked.Increment(ref RootCalls);
            return left + right;
        }

        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private sealed class Scope(double value, Exception? evaluationFailure,
        Exception? cleanupFailure) : INumericArithmeticScope
    {
        internal int Calls;
        internal int Disposals;

        public double Evaluate(NumericBinaryOperation operation, double left, double right)
        {
            Interlocked.Increment(ref Calls);
            ObjectDisposedException.ThrowIf(Disposals != 0, this);
            if (evaluationFailure is not null)
            {
                throw evaluationFailure;
            }

            return value;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            if (cleanupFailure is not null)
            {
                throw cleanupFailure;
            }
        }
    }

    private sealed class Host(Func<TimeSpan, CancellationToken, Task> delay) : IScriptHost
    {
        internal ConcurrentQueue<string> Output { get; } = new();

        public async ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            Output.Enqueue(text);
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) => delay(duration, token);
    }

    private sealed class FailingPauseAutomation(Exception pauseFailure,
        Exception cleanupFailure) : IWindowsAutomation
    {
        internal readonly TaskCompletionSource Attempted = Signal();
        internal int WorkerDisposals;
        internal int RootDisposals;

        public IWindowsAutomationSession CreateSession() => new Session(this, true);

        private sealed class Session(FailingPauseAutomation owner, bool root)
            : IWindowsAutomationSession
        {
            public IWindowsAutomationSession Fork() => new Session(owner, false);

            public void ConfigurePause(ScriptPauseControl? control, CancellationToken token)
            {
                if (!root)
                {
                    owner.Attempted.TrySetResult();
                    throw owner.PauseFailure;
                }
            }

            public ValueTask<Value> InvokeAsync(string name,
                IReadOnlyList<Value> arguments, CancellationToken token) =>
                throw new InvalidOperationException("Windows操作は禁止です。");

            public ValueTask DisposeAsync()
            {
                if (root)
                {
                    Interlocked.Increment(ref owner.RootDisposals);
                    return ValueTask.CompletedTask;
                }

                Interlocked.Increment(ref owner.WorkerDisposals);
                return ValueTask.FromException(owner.CleanupFailure);
            }
        }

        private Exception PauseFailure => pauseFailure;

        private Exception CleanupFailure => cleanupFailure;
    }
}
