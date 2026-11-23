using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ControlFlowTests
{
    [Fact]
    public async Task EmptyPrintWorksInBlockAndInlineIf()
    {
        Assert.Equal(["", "", "after"], await Run(
            "PRINT\nIF TRUE THEN PRINT\nIF FALSE THEN PRINT\n"
            + "PRINT \"after\""));
    }

    [Theory]
    [InlineData("IF 1 THEN PRINT 10 ELSE PRINT 20", "10")]
    [InlineData("IF 0 THEN PRINT 10 ELSE PRINT 20", "20")]
    [InlineData("IF(1)THEN PRINT \"THEN ELSE\"", "THEN ELSE")]
    [InlineData("IFB 0\nPRINT 10\nELSEIF 2 THEN\nPRINT 20\nELSE\nPRINT 30\nENDIF", "20")]
    [InlineData("IF 1\nPRINT 1\nELSEIF 1/0\nPRINT 2\nENDIF", "1")]
    [InlineData("IF 0\nPRINT 1/0\nENDIF\nPRINT 3", "3")]
    [InlineData("IF 1\nIF 0\nPRINT 0\nELSE\nPRINT 7\nENDIF\nENDIF", "7")]
    [InlineData("SELECT 2\nCASE 1\nPRINT 1\nCASE 2,1/0\nPRINT 2\nCASE 1/0\nPRINT 3\nSELEND", "2")]
    [InlineData("SELECT \"Ab\"\nCASE \"ab\"\nPRINT 1\nDEFAULT\nPRINT 0\nSELEND", "1")]
    [InlineData("SELECT EMPTY\nCASE 0\nPRINT 0\nCASE EMPTY\nPRINT 1\nSELEND", "1")]
    [InlineData("SELECT 9\nCASE 1\nPRINT 1\nDEFAULT\nPRINT 9\nSELEND", "9")]
    public async Task ChoosesOnlyOneBranch(string source,
        string expected) => Assert.Equal(new[] { expected },
        await Run(source));

    [Theory]
    [InlineData("sum=0\nFOR i=1 TO 3\nsum=sum+i\nNEXT\nPRINT sum\nPRINT i", "6", "4")]
    [InlineData("sum=0\nFOR i=3 TO 1 STEP -1\nsum=sum+i\nNEXT\nPRINT sum\nPRINT i", "6", "0")]
    [InlineData("sum=0\nFOR i=3 TO 1\nsum=sum+1\nNEXT\nPRINT sum\nPRINT i", "0", "3")]
    [InlineData("sum=0\nFOR i=0 TO 1 STEP 0.5\nsum=sum+1\nNEXT\nPRINT sum\nPRINT i", "3", "1.5")]
    [InlineData("n=0\nWHILE n<3\nn=n+1\nWEND\nPRINT n\nPRINT 9", "3", "9")]
    [InlineData("n=0\nREPEAT\nn=n+1\nUNTIL n>=3\nPRINT n\nPRINT 9", "3", "9")]
    [InlineData("n=0\nREPEAT\nn=n+1\nUNTIL TRUE\nPRINT n\nPRINT 9", "1", "9")]
    [InlineData("n=0\nWHILE FALSE\nn=n+1\nWEND\nPRINT n\nPRINT 9", "0", "9")]
    public async Task IteratesAndPreservesCounter(
        string source, string first, string second) => Assert.Equal(
        new[] { first, second }, await Run(source));

    [Fact]
    public async Task CapturesForLimitAndStepOnce()
    {
        string[] output = await Run(
            "limit=3\ndelta=1\nFOR i=1 TO limit STEP delta\nPRINT i\nlimit=1\ndelta=9\nNEXT");
        Assert.Equal(new[] { "1", "2", "3" }, output);
    }

    [Fact]
    public async Task ReadsForCounterAfterBodyMutation() => Assert.Equal(new[] { "1", "6" }, await Run("FOR i=1 TO 3\nPRINT i\ni=5\nNEXT\nPRINT i"));

    [Fact]
    public async Task ContinueInForStillIncrements() => Assert.Equal(new[] { "1", "3" }, await Run("FOR i=1 TO 3\nIF i=2 THEN CONTINUE\nPRINT i\nNEXT"));

    [Fact]
    public async Task ContinueInRepeatStillTestsUntil() => Assert.Equal(new[] { "3" }, await Run("i=0\nREPEAT\ni=i+1\nCONTINUE\nUNTIL i=3\nPRINT i"));

    [Theory]
    [InlineData("BREAK 2", "1", "9")]
    [InlineData("CONTINUE 2", "1", "2", "3", "9")]
    public async Task PropagatesMultipleLoopLevels(
        string jump, params string[] expected) => Assert.Equal(
        expected, await Run(
        $"FOR i=1 TO 3\nWHILE TRUE\nSELECT i\nCASE i\nPRINT i\n{jump}\nSELEND\nWEND\nPRINT 100\nNEXT\nPRINT 9"));

    [Fact]
    public async Task ForInCollectsStringOnceAndWritesEmptyAtEnd() => Assert.Equal(new[] { "A", "😀", "B", "" }, await Run("text=\"A😀B\"\nFOR c IN text\nPRINT c\ntext=\"X\"\nNEXT\nPRINT c"));

    [Fact]
    public async Task ForInEmptyCollectionClearsCounter() => Assert.Equal(new[] { "" }, await Run("c=\"old\"\nFOR c IN \"\"\nPRINT 1\nNEXT\nPRINT c"));

    [Fact]
    public async Task ForInBreakPreservesCurrentValue() => Assert.Equal(new[] { "A" }, await Run("FOR c IN \"AB\"\nBREAK\nNEXT\nPRINT c"));

    [Fact]
    public async Task FinallyRunsOnBreakContinueAndExit()
    {
        Assert.Equal(new[] { "1", "2", "end" }, await Run(
            "FOR i=1 TO 3\nTRY\nIF i=1 THEN CONTINUE\nBREAK\nFINALLY\nPRINT i\nENDTRY\nNEXT\nTRY\nEXIT\nFINALLY\nPRINT \"end\"\nENDTRY\nPRINT 100"));
    }

    [Fact]
    public async Task ExceptCatchesCalleeErrorWithOriginalLine() => Assert.Equal(new[] { "9", "handled", "after" }, await Run("TRY\nFail()\nEXCEPT\nPRINT TRY_ERRLINE\nPRINT \"handled\"\nENDTRY\nPRINT \"after\"\nPROCEDURE Fail()\nPRINT 1/0\nFEND"));

    [Fact]
    public async Task FinallyRunsBeforeOuterExcept() => Assert.Equal(new[] { "cleanup", "caught" }, await Run("TRY\nTRY\nPRINT 1/0\nFINALLY\nPRINT \"cleanup\"\nENDTRY\nEXCEPT\nPRINT \"caught\"\nENDTRY"));

    [Fact]
    public async Task HostFailureBypassesScriptExcept()
    {
        var host = new TestHost
        {
            Failure = new InvalidOperationException("host")
        };
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new Interpreter().RunAsync(
            "TRY\nPRINT 1\nEXCEPT\nPRINT 2\nENDTRY", host));
        Assert.Same(host.Failure, error);
        Assert.Empty(host.Output);
    }

    [Fact]
    public async Task CancelsLoopAtNextStatement()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new TestHost
        {
            OnOutput = () => cancellation.Cancel()
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter().RunAsync(
            "TRY\nWHILE TRUE\nPRINT 1\nWEND\nEXCEPT\nPRINT 2\nENDTRY",
            host, cancellation.Token));
        Assert.Equal(new[] { "1" }, host.Output);
    }

    [Theory]
    [InlineData("IF TRUE\nPRINT 1", 1)]
    [InlineData("WHILE TRUE\nNEXT", 2)]
    [InlineData("FOR i=1 TO 3\nNEXT i", 2)]
    [InlineData("BREAK", 1)]
    [InlineData("WHILE TRUE\nCONTINUE 2\nWEND", 2)]
    [InlineData("IF 0\nPRINT (1+\nENDIF", 2)]
    [InlineData("SELECT 1\nDEFAULT\nCASE 2\nSELEND", 3)]
    [InlineData("TRY\nPRINT 1\nENDTRY", 1)]
    [InlineData("IF 1\nPRINT 1\nELSEIF 1 THEN PRINT 2\nENDIF", 3)]
    public void RejectsMalformedStructureWithoutExecution(
        string source, int line) => Assert.Equal(line,
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source)).Line);

    [Fact]
    public void CompilationDoesNotEvaluateOrRunHostEffects()
    {
        ScriptProgram program = ScriptCompiler.Compile(
            "SLEEP(30)\nPRINT 1/0\nWhile TRUE\nWEND");
        Assert.NotNull(program);
    }

    [Fact]
    public void CompilationHonorsCancellationEvenForEmptySource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ScriptCompiler.Compile("", cancellation.Token));
    }

    [Fact]
    public async Task CompiledProgramCanBeReusedWithIndependentState()
    {
        ScriptProgram program = ScriptCompiler.Compile(
            "DIM x\nx=x+1\nPRINT x");
        var first = new TestHost();
        var second = new TestHost();
        var runner = new Interpreter();
        await Task.WhenAll(Task.Run(() => runner.RunAsync(
            program, first)), Task.Run(() => runner.RunAsync(program,
            second)));
        Assert.Equal(new[] { "1" }, first.Output);
        Assert.Equal(first.Output, second.Output);
    }

    [Fact]
    public async Task ZeroStepFailsEvenWhenRangeIsEmpty() => Assert.Equal(1, (await Assert.ThrowsAsync<ScriptException>(() => Run("FOR i=3 TO 1 STEP 0\nNEXT"))).Line);

    [Fact]
    public async Task ForExpressionsEvaluateInOrderAndOnce() => Assert.Equal(new[] { "1", "2", "3" }, await Run("PUBLIC calls=0\nFOR i=Tick() TO Tick() STEP Tick()\nNEXT\nFUNCTION Tick()\ncalls=calls+1\nPRINT calls\nRESULT=calls\nFEND"));

    [Fact]
    public async Task EmptyInfiniteLoopCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var task = Task.Run(() => new Interpreter().RunAsync("WHILE TRUE\nWEND", new TestHost(), cancellation.Token));
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FinallyRunsWhenUnhandledScriptErrorEscapes()
    {
        var host = new TestHost();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => new Interpreter().RunAsync(
            "TRY\nPRINT 1/0\nFINALLY\nPRINT \"cleanup\"\nENDTRY", host));
        Assert.Equal(2, error.Line);
        Assert.Equal(new[] { "cleanup" }, host.Output);
    }

    [Fact]
    public void RejectsExcessiveExpressionNestingWithDiagnostic() => Assert.Throws<ScriptException>(() => ScriptCompiler.Compile("PRINT " + new string('(', 150) + "1" + new string(')', 150)));

    internal static async Task<string[]> Run(string source)
    {
        var host = new TestHost();
        await new Interpreter().RunAsync(source, host);
        return [.. host.Output];
    }

    internal sealed class TestHost : IScriptHost
    {
        public List<string> Output { get; } = [];

        public Action? OnOutput
        {
            get; init;
        }

        public Exception? Failure
        {
            get; init;
        }

        public ValueTask WriteOutputAsync(string text,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            Output.Add(text);
            OnOutput?.Invoke();
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
