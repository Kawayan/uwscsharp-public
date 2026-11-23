using System.Collections;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ExceptionReportAcceptanceTests
{
    private sealed class Snapshot(string stage, Exception failure) : IReadOnlyList<string>
    {
        private static readonly IEnumerable<string> Values = ["snapshot"];

        public int Count => stage == "count" ? throw failure : 1;

        public string this[int index] => "snapshot";

        public IEnumerator<string> GetEnumerator() => stage == "enumeration"
            ? throw failure : Values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Windows(string stage, Exception? primary,
        Exception? snapshot, bool known, CancellationTokenSource cancellation)
        : IWindowsAutomation, IWindowsAutomationSession
    {
        private int calls;
        internal int Mappings;
        internal bool Created;
        internal bool Disposed;
        internal Exception? DisposeFailure;

        public IWindowsAutomationSession CreateSession()
        {
            Created = true;
            return this;
        }

        public IReadOnlyList<string> AllItems => calls == 1 ? ["before"]
            : snapshot is null ? ["after"]
            : stage == "getter" ? throw snapshot : new Snapshot(stage, snapshot);

        public string? GetScriptErrorMessage(string name, Exception failure)
        {
            Mappings++;
            return known && ReferenceEquals(primary, failure) ? failure.Message : null;
        }

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            if (++calls == 2 && primary is not null)
            {
                if (primary is OperationCanceledException)
                {
                    cancellation.Cancel();
                }

                return ValueTask.FromException<Value>(primary);
            }

            return ValueTask.FromResult(Value.Numeric(calls == 1 ? 1 : 0));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeFailure is null ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
        }
    }

    public static TheoryData<string, string> CombinedFailures
    {
        get
        {
            TheoryData<string, string> result = [];
            foreach (string stage in new[] { "getter", "count", "enumeration" })
            {
                foreach (string kind in new[] { "operation", "host", "script", "cancel" })
                {
                    result.Add(stage, kind);
                }
            }

            return result;
        }
    }

    private const string Script = "OPTION OPTFINALLY\nGETITEM(1,ITM_BTN)\nTRY\nTRY\n"
        + "GETITEM(1,ITM_BTN)\nEXCEPT\nPRINT TRY_ERRLINE\nPRINT \"caught\"\n"
        + "ENDTRY\nFINALLY\nPRINT ALL_ITEM_LIST[0]\nENDTRY\nPRINT \"after\"";

    [Theory]
    [MemberData(nameof(CombinedFailures))]
    public async Task SnapshotFailureDoesNotReplaceOperationHostOrCancellation(
        string stage, string kind)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = kind switch
        {
            "script" => new ScriptException(999, "backend内部の同型例外"),
            "cancel" => new OperationCanceledException(cancellation.Token),
            _ => new InvalidOperationException("操作またはhost失敗")
        };
        var snapshot = new InvalidOperationException("snapshot失敗");
        var windows = new Windows(stage, primary, snapshot,
            kind == "operation", cancellation);
        List<string> output = [];
        var runner = new Interpreter(windows: windows);
        if (kind == "operation")
        {
            await runner.RunAsync(Script, output.Add, cancellation.Token);
            Assert.Equal(["5", "caught", "before", "after"], output);
        }
        else
        {
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() =>
                runner.RunAsync(Script, output.Add, cancellation.Token));
            Assert.Same(primary, actual);
            Assert.Equal(["before"], output);
            if (actual is OperationCanceledException cancelled)
            {
                Assert.Equal(cancellation.Token, cancelled.CancellationToken);
            }
        }

        AggregateException errors = Assert.IsType<AggregateException>(
            primary.Data["UWSCSharp.SnapshotErrors"]);
        Assert.Same(snapshot, Assert.Single(errors.InnerExceptions));
        Assert.True(windows.Disposed);
    }

    [Theory]
    [InlineData("getter")]
    [InlineData("count")]
    [InlineData("enumeration")]
    public async Task SnapshotOnlyFailureRemainsHostFailureAndKeepsPriorPublication(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var snapshot = new InvalidOperationException("snapshot失敗");
        var windows = new Windows(stage, null, snapshot, false, cancellation);
        List<string> output = [];
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Interpreter(windows: windows).RunAsync(Script, output.Add));
        Assert.Same(snapshot, actual);
        Assert.Equal(["before"], output);
        Assert.True(windows.Disposed);
    }

    [Fact]
    public async Task FalseResultIsNotLogicalErrorReport()
    {
        using var cancellation = new CancellationTokenSource();
        var windows = new Windows("getter", null, null, true, cancellation);
        List<string> output = [];
        await new Interpreter(windows: windows).RunAsync(
            "GETITEM(1,ITM_BTN)\nTRY\nPRINT GETITEM(1,ITM_BTN)\nEXCEPT\n"
            + "PRINT \"caught\"\nENDTRY\nPRINT \"after\"", output.Add);
        Assert.Equal(["0", "after"], output);
        Assert.Equal(0, windows.Mappings);
        Assert.True(windows.Disposed);
    }

    [Theory]
    [InlineData("TRY\nENDTRY", 1)]
    [InlineData("TRY\nEXCEPT\nENDTRY\nFINALLY", 4)]
    [InlineData("TRY\nFINALLY\nEXCEPT\nENDTRY", 3)]
    [InlineData("TRY 1\nEXCEPT\nENDTRY", 1)]
    [InlineData("TRY\nEXCEPT 1\nENDTRY", 2)]
    [InlineData("TRY\nFINALLY\nENDTRY 1", 3)]
    public async Task InvalidHandlerGrammarIsRejectedBeforeSessions(
        string script, int line)
    {
        using var cancellation = new CancellationTokenSource();
        var windows = new Windows("getter", null, null, false, cancellation);
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: windows).RunAsync(script, _ => { }));
        Assert.Equal(line, failure.Line);
        Assert.False(windows.Created);
        Assert.False(windows.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostFailureInsideExceptIsNotCaughtAsScriptError(bool sameType)
    {
        Exception primary = sameType
            ? new ScriptException(777, "host内部の同型例外")
            : new InvalidOperationException("host障害");
        List<string> output = [];
        Exception actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            new Interpreter().RunAsync("TRY\nTRY\nTRY\nPRINT 1/0\nEXCEPT\n"
                + "PRINT \"handler\"\nENDTRY\nEXCEPT\nPRINT \"unexpected\"\n"
                + "ENDTRY\nFINALLY\nPRINT \"cleanup\"\nENDTRY", text =>
            {
                output.Add(text);
                if (text == "handler")
                {
                    throw primary;
                }
            }));
        Assert.Same(primary, actual);
        Assert.Equal(["handler", "cleanup"], output);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationInsideExceptKeepsPolicyAndDisposes(
        bool enabled, bool cleanupFails)
    {
        using var cancellation = new CancellationTokenSource();
        var windows = new Windows("getter", null, null, false, cancellation);
        List<string> output = [];
        string source = (enabled ? "OPTION OPTFINALLY\n" : "")
            + "TRY\nTRY\nPRINT 1/0\nEXCEPT\nPRINT \"handler\"\n"
            + "PRINT \"unreachable\"\nENDTRY\nFINALLY\nPRINT \"cleanup\"\n"
            + (cleanupFails ? "PRINT 1/0\n" : "") + "ENDTRY";
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: windows).RunAsync(source, text =>
            {
                output.Add(text);
                if (text == "handler")
                {
                    cancellation.Cancel();
                }
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(enabled ? ["handler", "cleanup"] : new[] { "handler" }, output);
        if (enabled && cleanupFails)
        {
            AggregateException errors = Assert.IsType<AggregateException>(
                failure.Data["UWSCSharp.FinallyErrors"]);
            Assert.IsType<ScriptException>(Assert.Single(errors.InnerExceptions));
        }

        Assert.True(windows.Disposed);
    }

    [Theory]
    [InlineData("try\nprint \"1\"\na=\"X\"; a=1/a\nprint \"2\"\nexcept\n"
        + "print \"3\"\nendtry\nprint \"4\"", "1,3,4")]
    [InlineData("try\ntry\nprint \"11\"\nexcept\nprint \"22\"\nendtry\n"
        + "finally\nprint \"33\"\nendtry\nprint \"44\"", "11,33,44")]
    public async Task OriginalHelpExceptionExamplesHaveExactExpectedOutput(
        string source, string expected)
    {
        List<string> output = [];
        await new Interpreter().RunAsync(source, output.Add);
        Assert.Equal(expected.Split(','), output);
    }

    private sealed class FailingTimer(bool recovers) : TimeProvider
    {
        internal int Calls;
        internal readonly Exception Failure = new InvalidOperationException("timer生成失敗");

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            if (++Calls == 1 || !recovers)
            {
                throw Failure;
            }

            return System.CreateTimer(callback, state, dueTime, period);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FinallyContextInitializationFailureKeepsCancellation(
        bool nested, bool recovers)
    {
        using var cancellation = new CancellationTokenSource();
        var windows = new Windows("getter", null, null, false, cancellation);
        var clock = new FailingTimer(recovers);
        string body = "TRY\nPRINT \"cancel\"\nFINALLY\nPRINT \"inner\"\nENDTRY";
        string source = "OPTION OPTFINALLY\n" + (nested
            ? "TRY\n" + body + "\nFINALLY\nPRINT \"outer\"\nENDTRY" : body);
        List<string> output = [];
        OperationCanceledException primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: windows, timeProvider: clock).RunAsync(source, text =>
            {
                output.Add(text);
                if (text == "cancel")
                {
                    cancellation.Cancel();
                }
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, primary.CancellationToken);
        Assert.Equal(nested && recovers ? ["cancel", "outer"] : new[] { "cancel" }, output);
        AggregateException errors = Assert.IsType<AggregateException>(
            primary.Data["UWSCSharp.FinallyErrors"]);
        Assert.Equal(nested && !recovers ? 2 : 1, errors.InnerExceptions.Count);
        Assert.All(errors.InnerExceptions, failure => Assert.Same(clock.Failure, failure));
        Assert.Equal(nested ? 2 : 1, clock.Calls);
        Assert.True(windows.Disposed);
    }

    private sealed class DisposeFailingClock(bool fails = true) : TimeProvider, IDisposable
    {
        private readonly bool throwOnDispose = fails;
        private readonly List<ITimer> timers = [];
        internal int Disposals;
        internal readonly Exception Failure = new InvalidOperationException("timer解放失敗");

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = System.CreateTimer(callback, state, dueTime, period);
            timers.Add(timer);
            return new Timer(timer, this);
        }

        public void Dispose()
        {
            foreach (ITimer timer in timers)
            {
                timer.Dispose();
            }
        }

        private sealed class Timer(ITimer inner, DisposeFailingClock owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                inner.Change(dueTime, period);

            public void Dispose()
            {
                inner.Dispose();
                owner.Disposals++;
                if (owner.throwOnDispose)
                {
                    throw owner.Failure;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FinallyContextDisposalKeepsCancellationAndAllDiagnostics(
        bool scriptFails, bool sessionFails)
    {
        using var cancellation = new CancellationTokenSource();
        var cleanup = new InvalidOperationException("session解放失敗");
        var windows = new Windows("getter", null, null, false, cancellation)
        {
            DisposeFailure = sessionFails ? cleanup : null
        };
        using var clock = new DisposeFailingClock();
        List<string> output = [];
        string source = "OPTION OPTFINALLY\nTRY\nPRINT \"cancel\"\nFINALLY\n"
            + "PRINT \"cleanup\"\n" + (scriptFails ? "PRINT 1/0\n" : "") + "ENDTRY";
        OperationCanceledException primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: windows, timeProvider: clock).RunAsync(source, text =>
            {
                output.Add(text);
                if (text == "cancel")
                {
                    cancellation.Cancel();
                }
            }, cancellation.Token));
        Assert.Equal(cancellation.Token, primary.CancellationToken);
        Assert.Equal(["cancel", "cleanup"], output);
        AggregateException errors = Assert.IsType<AggregateException>(
            primary.Data["UWSCSharp.FinallyErrors"]);
        Assert.Equal(scriptFails ? 2 : 1, errors.InnerExceptions.Count);
        Assert.Same(clock.Failure, errors.InnerExceptions[^1]);
        if (scriptFails)
        {
            Assert.IsType<ScriptException>(errors.InnerExceptions[0]);
        }

        if (sessionFails)
        {
            AggregateException sessions = Assert.IsType<AggregateException>(
                primary.Data["UWSCSharp.CleanupErrors"]);
            Assert.Same(cleanup, Assert.Single(sessions.InnerExceptions));
        }

        Assert.Equal(1, clock.Disposals);
        Assert.True(windows.Disposed);
    }

    [Fact]
    public async Task RoutineInitializerIsRejectedBeforeCreatingFinallyContext()
    {
        using var cancellation = new CancellationTokenSource();
        var windows = new Windows("getter", null, null, false, cancellation);
        using var clock = new DisposeFailingClock();
        List<string> output = [];
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter(windows: windows, timeProvider: clock).RunAsync(
                "OPTION OPTFINALLY\nPUBLIC initial=Initialize()\nPRINT \"unreachable\"\n"
                + "FUNCTION Initialize()\nTRY\nPRINT \"cancel\"\nFINALLY\n"
                + "PRINT \"cleanup\"\nENDTRY\nFEND", text =>
                {
                    output.Add(text);
                    if (text == "cancel")
                    {
                        cancellation.Cancel();
                    }
                }, cancellation.Token));
        Assert.Equal(2, failure.Line);
        Assert.Contains("初期値に使用できない式", failure.Message);
        Assert.Empty(output);
        Assert.Equal(0, clock.Disposals);
        Assert.False(windows.Created);
        Assert.False(windows.Disposed);
        Assert.False(cancellation.IsCancellationRequested);
    }
}
