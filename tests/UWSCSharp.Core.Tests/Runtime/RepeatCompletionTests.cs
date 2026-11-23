using System.Globalization;
using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class RepeatCompletionTests
{
    [Theory]
    [InlineData("0", 2)]
    [InlineData("-0", 2)]
    [InlineData("EMPTY", 2)]
    [InlineData("FALSE", 2)]
    [InlineData("TRUE", 1)]
    [InlineData("0.5", 1)]
    [InlineData("-0.5", 1)]
    [InlineData("1e-300", 1)]
    [InlineData("1e300", 1)]
    [InlineData("\"0\"", 2)]
    [InlineData("\"-0.5\"", 1)]
    [InlineData("\"$FFFFFFFF\"", 1)]
    [InlineData("\"1e2\"", 1)]
    [InlineData("\" 1 \"", 1)]
    public async Task BodyRunsBeforeConditionAndOnlyTrueTerminates(
        string condition, int iterations)
    {
        Assert.Equal([iterations.ToString(CultureInfo.InvariantCulture)], await Run(
            "n=0\nREPEAT\nn=n+1\nIF n=2 THEN BREAK\nUNTIL "
            + condition + "\nPRINT n"));
    }

    [Theory]
    [InlineData("\"TRUE\"")]
    [InlineData("\"bad\"")]
    [InlineData("\"\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1e999\"")]
    [InlineData("\"1<#NULL>\"")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    public async Task InvalidConditionReportsUntilAfterFirstBody(string condition)
    {
        var host = new TestHost();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("// heading\nREPEAT\nPRINT \"body\"\nUNTIL "
                + condition, host));
        Assert.Equal(4, error.Line);
        Assert.Equal(["body"], host.Output);
    }

    [Fact]
    public async Task EmptyBodyEvaluatesConditionOncePerIteration()
    {
        Assert.Equal(["4"], await Run("""
            PUBLIC calls=0
            REPEAT
            UNTIL Tick()=4
            PRINT calls
            FUNCTION Tick()
                calls=calls+1
                RESULT=calls
            FEND
            """));
    }

    [Fact]
    public async Task ContinueRunsFinallyBeforeUntil()
    {
        Assert.Equal(["3"], await Run("""
            n=0
            REPEAT
                TRY
                    CONTINUE
                    PRINT 1/0
                FINALLY
                    n=n+1
                ENDTRY
                PRINT 1/0
            UNTIL n=3
            PRINT n
            """));
    }

    [Theory]
    [InlineData("BREAK")]
    [InlineData("EXIT")]
    [InlineData("EXITEXIT")]
    public async Task LeavingBodySkipsUntil(string instruction)
    {
        Assert.Equal(["body"], await Run(
            "REPEAT\nPRINT \"body\"\n" + instruction
            + "\nUNTIL INT(NULL)"));
    }

    [Fact]
    public async Task OuterContinueTestsOnlyOuterUntil()
    {
        Assert.Equal(["3"], await Run("""
            n=0
            REPEAT
                REPEAT
                    n=n+1
                    CONTINUE 2
                UNTIL INT(NULL)
                PRINT 1/0
            UNTIL n=3
            PRINT n
            """));
    }

    [Fact]
    public async Task NestedPostTestsRunIndependently()
    {
        Assert.Equal(["6", "2", "3"], await Run("""
            i=0
            n=0
            REPEAT
                i=i+1
                j=0
                REPEAT
                    j=j+1
                    n=n+1
                UNTIL j=3
            UNTIL i=2
            PRINT n
            PRINT i
            PRINT j
            """));
    }

    [Theory]
    [InlineData("FALSE AND Tick()")]
    [InlineData("TRUE OR Tick()")]
    [InlineData("TRUE XOR Tick()")]
    public async Task ConditionUsesExistingEagerBitwiseOperators(string condition)
    {
        Assert.Equal(["1"], await Run(
            "PUBLIC calls=0\nREPEAT\nIF calls=1 THEN BREAK\nUNTIL "
            + condition + "\nPRINT calls\nFUNCTION Tick()\n"
            + "calls=calls+1\nRESULT=1\nFEND"));
    }

    [Theory]
    [InlineData("REPEAT 1\nUNTIL TRUE", 1)]
    [InlineData("REPEAT\nUNTIL", 2)]
    [InlineData("REPEAT", 1)]
    [InlineData("REPEAT\nWEND", 2)]
    [InlineData("UNTIL TRUE", 1)]
    [InlineData("REPEAT\nUNTIL 1 2", 2)]
    [InlineData("IF FALSE\nREPEAT\nUNTIL\nENDIF", 3)]
    public void InvalidStructureIsRejectedBeforeBody(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile(source));
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public async Task ExceptKeepsConditionAndBodySourceLines()
    {
        Assert.Equal(["4"], await Run(
            "n=1\nTRY\nREPEAT\nUNTIL NULL\nEXCEPT\n"
            + "PRINT TRY_ERRLINE\nENDTRY"));
        Assert.Equal(["4"], await Run(
            "n=1\nTRY\nREPEAT\nn=INT(NULL)\nUNTIL TRUE\nEXCEPT\n"
            + "PRINT TRY_ERRLINE\nENDTRY"));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public async Task NumericConditionUsesInvariantCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(["body"], await Run(
                "REPEAT\nPRINT \"body\"\nUNTIL \".5\""));
            await Assert.ThrowsAsync<ScriptException>(() =>
                Run("REPEAT\nUNTIL \"0,5\""));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task ExistingTextLengthBoundariesAreRetained()
    {
        string[] accepted = [new string('0', 31) + "1",
            ".5" + new string('0', 62)];
        foreach (string text in accepted)
        {
            Assert.Equal(["body"], await Run("REPEAT\nPRINT \"body\"\nUNTIL \""
                + text + "\""));
        }

        string[] rejected = [new string('0', 32) + "1",
            ".5" + new string('0', 63)];
        foreach (string text in rejected)
        {
            await Assert.ThrowsAsync<ScriptException>(() => Run(
                "REPEAT\nUNTIL \"" + text + "\""));
        }
    }

    [Fact]
    public async Task UnsupportedValueKindsAndNonfiniteAdapterResultsAreRejected()
    {
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(UwscArray.FromValues([Value.Numeric(1)])),
            Value.FromHashTable(UwscHashTable.Create()),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            var adapter = new ConditionAdapter(value);
            var host = new TestHost();
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter(windows: adapter).RunAsync(
                    "// heading\nREPEAT\nPRINT \"body\"\nUNTIL CHKBTN(1,\"x\")", host));
            Assert.Equal(4, error.Line);
            Assert.Equal(1, adapter.Calls);
            Assert.Equal(1, adapter.Disposals);
            Assert.Equal(["body"], host.Output);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationOnUntilReturnPreventsAnotherIteration(
        bool finallyEnabled, bool truth)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new ConditionAdapter(Value.Numeric(truth ? 1 : 0))
        {
            OnCall = cancellation.Cancel
        };
        var host = new TestHost();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                (finallyEnabled ? "OPTION OPTFINALLY\n" : "")
                + "TRY\nREPEAT\nPRINT \"body\"\nUNTIL CHKBTN(1,\"x\")\n"
                + "PRINT \"after\"\nFINALLY\nPRINT \"cleanup\"\nENDTRY", host,
                cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(finallyEnabled ? ["body", "cleanup"] : new[] { "body" }, host.Output);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task HelpMouseMoveExampleRecordsSixHundredOneMovesAndSevenCalls()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "uwsc-repeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(Path.Combine(folder, "TEST.uws"),
                "calls=calls+1");
            File.WriteAllText(main, """
                PUBLIC calls=0
                x=0
                REPEAT
                    MMV(x,200)
                    IF (x MOD 100)=0 THEN CALL .\TEST.uws
                    x=x+1
                    SLEEP(0.1)
                UNTIL x>600
                PRINT x
                PRINT calls
                """);
            var host = new MouseMoveExampleHost();
            await new Interpreter(windows: host).RunAsync(
                ScriptCompiler.CompileFile(main), host);
            Assert.Equal(["601", "7"], host.Output);
            Assert.Equal(601, host.Positions.Count);
            for (int i = 0; i < 601; i++)
            {
                Assert.Equal((i, 200), host.Positions[i]);
            }

            Assert.Equal(601, host.Delays.Count);
            Assert.All(host.Delays, delay =>
                Assert.Equal(TimeSpan.FromSeconds(.1), delay));
            Assert.Equal(1, host.Disposals);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private sealed class ConditionAdapter(Value value)
        : IWindowsAutomation, IWindowsAutomationSession
    {
        internal Action? OnCall
        {
            get; init;
        }

        internal int Calls
        {
            get; private set;
        }

        internal int Disposals
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Calls++;
            OnCall?.Invoke();
            return ValueTask.FromResult(value);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
