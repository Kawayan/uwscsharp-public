using System.Globalization;
using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class WhileCompletionTests
{
    [Theory]
    [InlineData("0", false)]
    [InlineData("-0", false)]
    [InlineData("EMPTY", false)]
    [InlineData("FALSE", false)]
    [InlineData("TRUE", true)]
    [InlineData("0.5", true)]
    [InlineData("-0.5", true)]
    [InlineData("1e-300", true)]
    [InlineData("1e300", true)]
    [InlineData("\"0\"", false)]
    [InlineData("\"-0.5\"", true)]
    [InlineData("\"$FFFFFFFF\"", true)]
    [InlineData("\"1e2\"", true)]
    [InlineData("\" 1 \"", true)]
    public async Task ConditionComparesFiniteNumberWithZero(string condition, bool truth)
    {
        Assert.Equal([truth ? "1" : "0"], await Run(
            "n=0\nWHILE " + condition + "\nn=n+1\nBREAK\nWEND\nPRINT n"));
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
    public async Task InvalidConditionFailsAtHeaderWithoutEnteringBody(string condition)
    {
        var host = new TestHost();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("// heading\nWHILE " + condition
                + "\nPRINT \"body\"\nBREAK\nWEND", host));
        Assert.Equal(2, error.Line);
        Assert.Empty(host.Output);
    }

    [Fact]
    public async Task EmptyBodyReevaluatesConditionIncludingFinalFalse()
    {
        Assert.Equal(["4"], await Run("""
            PUBLIC calls=0
            WHILE Tick()<4
            WEND
            PRINT calls
            FUNCTION Tick()
                calls=calls+1
                RESULT=calls
            FEND
            """));
    }

    [Fact]
    public async Task ContinueRunsFinallyBeforeNextCondition()
    {
        Assert.Equal(["3"], await Run("""
            n=0
            WHILE n<3
                TRY
                    CONTINUE
                    PRINT 1/0
                FINALLY
                    n=n+1
                ENDTRY
                PRINT 1/0
            WEND
            PRINT n
            """));
    }

    [Theory]
    [InlineData("BREAK")]
    [InlineData("EXIT")]
    public async Task LeavingLoopDoesNotEvaluateConditionAgain(string instruction)
    {
        Assert.Equal(["1"], await Run("""
            PUBLIC calls=0
            F()
            PRINT calls
            PROCEDURE F()
                WHILE Tick()
            """ + "\n" + instruction + """

                WEND
            FEND
            FUNCTION Tick()
                calls=calls+1
                IF calls>1 THEN PRINT INT(NULL)
                RESULT=1
            FEND
            """));
    }

    [Fact]
    public async Task OuterContinueSkipsInnerUntilAndRechecksWhile()
    {
        Assert.Equal(["3"], await Run("""
            n=0
            WHILE n<3
                REPEAT
                    n=n+1
                    CONTINUE 2
                UNTIL INT(NULL)
                PRINT 1/0
            WEND
            PRINT n
            """));
    }

    [Fact]
    public async Task NestedLoopResetsInnerStateEachTime()
    {
        Assert.Equal(["6", "2", "3"], await Run("""
            i=0
            n=0
            WHILE i<2
                i=i+1
                j=0
                WHILE j<3
                    j=j+1
                    n=n+1
                WEND
            WEND
            PRINT n
            PRINT i
            PRINT j
            """));
    }

    [Theory]
    [InlineData("FALSE AND Tick()", "1")]
    [InlineData("TRUE OR Tick()", "1")]
    [InlineData("TRUE XOR Tick()", "1")]
    public async Task ConditionUsesExistingEagerBitwiseOperators(
        string condition, string expectedCalls)
    {
        Assert.Equal([expectedCalls], await Run(
            "PUBLIC calls=0\nWHILE " + condition
            + "\nBREAK\nWEND\nPRINT calls\nFUNCTION Tick()\n"
            + "calls=calls+1\nRESULT=1\nFEND"));
    }

    [Theory]
    [InlineData("WHILE", 1)]
    [InlineData("WHILE TRUE", 1)]
    [InlineData("WHILE TRUE\nNEXT", 2)]
    [InlineData("WHILE FALSE\nWEND 1", 2)]
    [InlineData("WEND", 1)]
    [InlineData("IF FALSE\nWHILE\nWEND\nENDIF", 2)]
    public void InvalidStructureIsRejectedEvenWhenUnreached(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile(source));
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public async Task ReevaluationErrorRetainsHeaderAndBodyErrorRetainsBodyLine()
    {
        Assert.Equal(["3"], await Run(
            "n=1\nTRY\nWHILE n\nn=NULL\nWEND\nEXCEPT\n"
            + "PRINT TRY_ERRLINE\nENDTRY"));
        Assert.Equal(["4"], await Run(
            "n=1\nTRY\nWHILE n\nn=INT(NULL)\nWEND\nEXCEPT\n"
            + "PRINT TRY_ERRLINE\nENDTRY"));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public async Task NumericConditionDoesNotUseCurrentCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(["ok"], await Run(
                "WHILE \".5\"\nPRINT \"ok\"\nBREAK\nWEND"));
            await Assert.ThrowsAsync<ScriptException>(() =>
                Run("WHILE \"0,5\"\nBREAK\nWEND"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task ExistingTextLengthBoundaryIsAppliedToConditions()
    {
        string[] accepted = [new string('0', 31) + "1",
            ".5" + new string('0', 62)];
        foreach (string text in accepted)
        {
            Assert.Equal(["ok"], await Run("WHILE \"" + text
                + "\"\nPRINT \"ok\"\nBREAK\nWEND"));
        }

        string[] rejected = [new string('0', 32) + "1",
            ".5" + new string('0', 63)];
        foreach (string text in rejected)
        {
            await Assert.ThrowsAsync<ScriptException>(() => Run(
                "WHILE \"" + text + "\"\nBREAK\nWEND"));
        }
    }

    [Fact]
    public async Task EveryUnsupportedValueKindAndNonfiniteAdapterValueIsRejected()
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
                    "// heading\nWHILE CHKBTN(1,\"x\")\nPRINT 9\nBREAK\nWEND", host));
            Assert.Equal(2, error.Line);
            Assert.Equal(1, adapter.Calls);
            Assert.Equal(1, adapter.Disposals);
            Assert.Empty(host.Output);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationOnConditionReturnDoesNotEnterBody(
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
                + "TRY\nWHILE CHKBTN(1,\"x\")\nPRINT \"body\"\n"
                + "BREAK\nWEND\nFINALLY\nPRINT \"cleanup\"\nENDTRY", host,
                cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(finallyEnabled ? ["cleanup"] : Array.Empty<string>(), host.Output);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, adapter.Disposals);
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

    [Fact]
    public async Task HelpMouseMoveExampleRecordsSixHundredMovesAndSixCalls()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "uwsc-while-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(Path.Combine(folder, "TEST.uws"),
                "calls=calls+1");
            File.WriteAllText(main, """
                PUBLIC calls=0
                x=0
                WHILE x<600
                    MMV(x,200)
                    IF (x MOD 100)=0 THEN CALL .\TEST.uws
                    x=x+1
                    SLEEP(0.1)
                WEND
                PRINT x
                PRINT calls
                """);
            var host = new MouseMoveExampleHost();
            await new Interpreter(windows: host).RunAsync(
                ScriptCompiler.CompileFile(main), host);
            Assert.Equal(["600", "6"], host.Output);
            Assert.Equal(600, host.Positions.Count);
            for (int i = 0; i < 600; i++)
            {
                Assert.Equal((i, 200), host.Positions[i]);
            }

            Assert.Equal(600, host.Delays.Count);
            Assert.All(host.Delays, delay =>
                Assert.Equal(TimeSpan.FromSeconds(.1), delay));
            Assert.Equal(1, host.Disposals);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

}
