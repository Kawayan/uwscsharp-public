using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ExpressionTests
{
    [Theory]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("6 AND 3", 2)]
    [InlineData("4 OR 1", 5)]
    [InlineData("7 XOR 3", 4)]
    [InlineData("10 MOD 3", 1)]
    [InlineData("!FALSE", 1)]
    [InlineData("\"2.5\"*2", 5)]
    [InlineData("2*\"3\"", 6)]
    [InlineData("\"3\"-1", 2)]
    [InlineData("\"4\"/2", 2)]
    [InlineData("\"10\"<11", 1)]
    [InlineData("\"Ab\"=\"aB\"", 1)]
    public void EvaluatesOperators(string expression,
        double expected) => Assert.Equal(Value.Numeric(expected),
        ExpressionEvaluator.Evaluate(expression));

    [Fact]
    public void ResolvesNamesWithoutChangingCallerDictionary()
    {
        var variables = new Dictionary<string, Value>
        {
            ["Total"] = Value.Numeric(3)
        };
        Assert.Equal(Value.Numeric(6),
            ExpressionEvaluator.Evaluate("TOTAL*2", variables));
        Assert.Single(variables);
        Assert.True(variables.ContainsKey("Total"));
    }

    [Fact]
    public void RetainsNullAndEmptyAsDistinctValues()
    {
        Assert.NotEqual(ExpressionEvaluator.Evaluate(
            "NULL"), ExpressionEvaluator.Evaluate("EMPTY"));
        Assert.Equal(0, ExpressionEvaluator.Evaluate(
            "EMPTY").AsNumber());
        Assert.Throws<InvalidOperationException>(() => ExpressionEvaluator.Evaluate("NULL").AsNumber());
    }

    [Theory]
    [InlineData("1/0")]
    [InlineData("0 AND (1/0)")]
    [InlineData("1e999")]
    [InlineData("missing")]
    [InlineData("\"abc\"*2")]
    [InlineData("\"2\"/\"0\"")]
    public void RejectsInvalidExpressions(
        string expression) => Assert.Throws<InvalidOperationException>(
        () => ExpressionEvaluator.Evaluate(expression));

    [Theory]
    [InlineData("1+\"a\"", "1a")]
    [InlineData("\"a\"+1", "a1")]
    [InlineData("\"1\"+2", "12")]
    [InlineData("EMPTY+\"a\"", "a")]
    public void MixedAdditionConcatenates(string expression,
        string expected) => Assert.Equal(Value.String(expected),
        ExpressionEvaluator.Evaluate(expression));
}

public sealed class SourceTests
{
    [Fact]
    public void SplitsStatementsOutsideQuotesAndPreservesPhysicalLine()
    {
        (int Line, string Text)[] statements = [.. Source.Statements(
            "// heading\nx=1 + _ // continue\n2; PRINT \"a;//b\"")];
        Assert.Equal(2, statements.Length);
        Assert.Equal((2, "x=1 + 2"), statements[0]);
        Assert.Equal((2, "PRINT \"a;//b\""), statements[1]);
    }

    [Fact]
    public void ReportsUnfinishedContinuation() => Assert.Equal(1, Assert.Throws<ScriptException>(() => Source.Statements("PRINT 1 _").ToArray()).Line);
}

public sealed class InterpreterTests
{
    [Fact]
    public async Task RoutesEffectsInOrderWithoutRealWaiting()
    {
        var host = new RecordingHost();
        await new Interpreter().RunAsync(
            "PRINT 1\nSLEEP(30)\nPRINT 2", host);
        Assert.Equal(
            new[] { "print:1", "delay", "print:2" }, host.Events);
        Assert.Equal(TimeSpan.FromSeconds(30), host.Delay);
    }

    [Fact]
    public async Task CancellationFromWaitPreventsFollowingOutput()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new RecordingHost
        {
            OnDelay = () => cancellation.Cancel()
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter().RunAsync("SLEEP(30)\nPRINT 2", host,
            cancellation.Token));
        Assert.Equal(new[] { "delay" }, host.Events);
        Assert.Equal(cancellation.Token, host.Token);
    }

    [Fact]
    public async Task AlreadyCancelledEmptyScriptStillCancels()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter().RunAsync("", new RecordingHost(),
            cancellation.Token));
    }

    [Fact]
    public async Task HostFailureIsNotReportedAsScriptSyntaxError()
    {
        var failure = new InvalidOperationException(
            "UI output failed");
        var host = new RecordingHost
        {
            OutputFailure = failure
        };
        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Interpreter().RunAsync("PRINT 1", host));
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task ReportsOriginalLineForExpressionFailure()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => new Interpreter().RunAsync(
            "// comment\nPRINT 1 + _\n(1/0)", new RecordingHost()));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task ConcurrentRunsHaveIndependentVariablesAndHosts()
    {
        IScriptRunner runner = new Interpreter();
        var first = new RecordingHost();
        var second = new RecordingHost();
        await Task.WhenAll(Task.Run(() => runner.RunAsync(
            "x=1\nSLEEP(0)\nPRINT x", first)), Task.Run(() => runner.RunAsync("x=2\nSLEEP(0)\nPRINT x", second)));
        Assert.Equal(new[] { "delay", "print:1" }, first.Events);
        Assert.Equal(new[] { "delay", "print:2" },
            second.Events);
    }

    private sealed class RecordingHost : IScriptHost
    {
        public List<string> Events { get; } = [];

        public TimeSpan Delay
        {
            get; private set;
        }

        public CancellationToken Token
        {
            get; private set;
        }

        public Action? OnDelay
        {
            get; init;
        }

        public Exception? OutputFailure
        {
            get; init;
        }

        public ValueTask WriteOutputAsync(string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OutputFailure is not null)
            {
                throw OutputFailure;
            }

            Events.Add("print:" + text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration,
            CancellationToken cancellationToken)
        {
            Events.Add("delay");
            Delay = duration;
            Token = cancellationToken;
            OnDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
