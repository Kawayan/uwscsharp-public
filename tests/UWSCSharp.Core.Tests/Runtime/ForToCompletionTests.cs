using System.Globalization;
using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ForToCompletionTests
{
    [Theory]
    [InlineData("1", "3", "1", "3", "4")]
    [InlineData("3", "1", "-1", "3", "0")]
    [InlineData("2", "2", "1", "1", "3")]
    [InlineData("3", "1", "1", "0", "3")]
    [InlineData("1", "3", "-1", "0", "1")]
    [InlineData(".5", "1", ".25", "3", "1.25")]
    [InlineData("-3", "-1", "1", "3", "0")]
    [InlineData("\"1\"", "\"3\"", "\"1\"", "3", "4")]
    [InlineData("FALSE", "TRUE", "1", "2", "2")]
    [InlineData("EMPTY", "1", "1", "2", "2")]
    [InlineData("1", "2", "10", "1", "11")]
    public async Task FiniteRangesRetainTheirPostIncrementCounter(
        string start, string end, string step, string iterations, string counter)
    {
        Assert.Equal([iterations, counter], await Run(
            $"n=0\nFOR i={start} TO {end} STEP {step}\nn=n+1\nNEXT\nPRINT n\nPRINT i"));
    }

    [Fact]
    public async Task HeaderEvaluatesInOrderOnceAndObservesPublishedCounter()
    {
        Assert.Equal(["2", "3", "4", "SE1P2"], await Run("""
            PUBLIC events=""
            PUBLIC i=9
            FOR i=StartValue() TO EndValue() STEP StepValue()
                PRINT i
            NEXT
            PRINT i
            PRINT events
            FUNCTION StartValue()
                events=events+"S"
                RESULT=1
            FEND
            FUNCTION EndValue()
                events=events+"E"+i
                i=2
                RESULT=3
            FEND
            FUNCTION StepValue()
                events=events+"P"+i
                RESULT=1
            FEND
            """));
    }

    [Fact]
    public async Task LimitAndStepAreCapturedBeforeBodyMutatesTheirSources()
    {
        Assert.Equal(["1", "2", "3", "4"], await Run("""
            limit=3
            stepValue=1
            FOR i=1 TO limit STEP stepValue
                PRINT i
                limit=100
                stepValue=-1
            NEXT
            PRINT i
            """));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("\"3\"")]
    public async Task BodyCounterMutationIsReadBeforeAddingStep(string replacement)
    {
        Assert.Equal(["1", "4"], await Run(
            "FOR i=1 TO 3\nPRINT i\ni=" + replacement + "\nNEXT\nPRINT i"));
    }

    [Fact]
    public async Task ContinueRunsFinallyThenIncrementsCurrentCounter()
    {
        Assert.Equal(["1", "5"], await Run("""
            n=0
            FOR i=1 TO 4
                TRY
                    CONTINUE
                    PRINT 1/0
                FINALLY
                    n=n+1
                    i=4
                ENDTRY
            NEXT
            PRINT n
            PRINT i
            """));
    }

    [Theory]
    [InlineData("BREAK")]
    [InlineData("EXIT")]
    [InlineData("EXITEXIT")]
    public async Task LeavingBodySkipsInvalidCounterConversion(string instruction)
    {
        Assert.Equal(["body"], await Run(
            "FOR i=1 TO 3\ni=NULL\nPRINT \"body\"\n"
            + instruction + "\nNEXT"));
    }

    [Theory]
    [InlineData("BREAK 2", "1", "1", "1")]
    [InlineData("CONTINUE 2", "2", "3", "1")]
    public async Task MultiLevelFlowUpdatesOnlyRequiredCounter(
        string instruction, string count, string outer, string inner)
    {
        Assert.Equal([count, outer, inner], await Run(
            "n=0\nFOR i=1 TO 2\nFOR j=1 TO 3\nn=n+1\n"
            + instruction + "\nNEXT\nNEXT\nPRINT n\nPRINT i\nPRINT j"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("\"0\"")]
    [InlineData("EMPTY")]
    public async Task ZeroStepIsRejectedEvenWhenRangeIsEmpty(string step)
    {
        var host = new TestHost();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("// heading\nFOR i=3 TO 1 STEP "
                + step + "\nPRINT 9\nNEXT", host));
        Assert.Equal(2, error.Line);
        Assert.Empty(host.Output);
    }

    [Fact]
    public async Task IncrementRejectsStallingAndOverflowInsteadOfLooping()
    {
        foreach (string header in new[]
        {
            "FOR i=9007199254740992 TO 9007199254740992",
            "FOR i=1e308 TO 1e308 STEP 1e308"
        })
        {
            var host = new TestHost();
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter().RunAsync(header + "\nPRINT \"body\"\nNEXT", host));
            Assert.Equal(1, error.Line);
            Assert.Equal(["body"], host.Output);
        }
    }

    [Fact]
    public async Task DoubleAccumulationAndSubnormalStepsKeepCurrentArithmetic()
    {
        Assert.Equal(["3"], await Run(
            "n=0\nFOR i=0 TO .3 STEP .1\nn=n+1\nNEXT\nPRINT n"));
        Assert.Equal(["3"], await Run(
            "n=0\nFOR i=0 TO 1e-323 STEP 5e-324\nn=n+1\nNEXT\nPRINT n"));
    }

    [Theory]
    [InlineData("FOR i= TO 3\nNEXT", 1)]
    [InlineData("FOR i=1\nNEXT", 1)]
    [InlineData("FOR i=1 TO\nNEXT", 1)]
    [InlineData("FOR i=1 TO 3 STEP\nNEXT", 1)]
    [InlineData("FOR i=1 TO 3\nNEXT i", 2)]
    [InlineData("NEXT", 1)]
    [InlineData("FOR i=1 TO 3\nWEND", 2)]
    [InlineData("FOR i=1 TO 3", 1)]
    [InlineData("IF FALSE\nFOR i=1 TO\nNEXT\nENDIF", 2)]
    public void InvalidStructureIsRejectedBeforeExecution(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile(source));
        Assert.Equal(line, error.Line);
    }

    [Theory]
    [InlineData("CONST i=1\nFOR i=1 TO 3\nNEXT", 2)]
    [InlineData("OPTION EXPLICIT\nFOR i=1 TO 3\nNEXT", 2)]
    public async Task CounterUsesNormalWriteRestrictions(string source, int line)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => Run(source));
        Assert.Equal(line, error.Line);
        Assert.Equal(["4"], await Run(
            "OPTION EXPLICIT\nDIM i\nFOR i=1 TO 3\nNEXT\nPRINT i"));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public async Task NumericTextDoesNotUseCurrentCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(["0.5", "1"], await Run(
                "FOR i=\".5\" TO \"1\" STEP \".5\"\nPRINT i\nNEXT"));
            await Assert.ThrowsAsync<ScriptException>(() =>
                Run("FOR i=\"0,5\" TO 1\nNEXT"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task UnsupportedKindsFailAtEachHeaderSlotAndDisposeSession()
    {
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(UwscArray.FromValues([Value.Numeric(1)])),
            Value.FromHashTable(UwscHashTable.Create()),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (string header in new[]
        {
            "FOR i=CHKBTN(1,\"x\") TO 2",
            "FOR i=1 TO CHKBTN(1,\"x\")",
            "FOR i=1 TO 2 STEP CHKBTN(1,\"x\")"
        })
        {
            foreach (Value value in invalid)
            {
                var adapter = new HeaderAdapter(value);
                var host = new TestHost();
                ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                    new Interpreter(windows: adapter).RunAsync(
                        "// heading\n" + header + "\nPRINT 9\nNEXT", host));
                Assert.Equal(2, error.Line);
                Assert.Equal(1, adapter.Calls);
                Assert.Equal(1, adapter.Disposals);
                Assert.Empty(host.Output);
            }
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("step")]
    public async Task CancellationDuringHeaderStopsBeforeBody(string slot)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new HeaderAdapter(Value.Numeric(1))
        {
            OnCall = cancellation.Cancel
        };
        string header = slot switch
        {
            "start" => "FOR i=CHKBTN(1,\"x\") TO 3",
            "end" => "FOR i=1 TO CHKBTN(1,\"x\")",
            _ => "FOR i=1 TO 3 STEP CHKBTN(1,\"x\")"
        };
        var host = new TestHost();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                header + "\nPRINT 9\nNEXT", host, cancellation.Token));
        Assert.Empty(host.Output);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, adapter.Disposals);
    }

    [Fact]
    public async Task HelpMouseMoveExampleRecordsSixHundredOneMovesAndSevenCalls()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "uwsc-for-to-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(Path.Combine(folder, "TEST.uws"), "calls=calls+1");
            File.WriteAllText(main, """
                PUBLIC calls=0
                FOR x=0 TO 600
                    MMV(x,200)
                    IF (x MOD 100)=0 THEN CALL .\TEST.uws
                    SLEEP(0.1)
                NEXT
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

    private sealed class HeaderAdapter(Value value)
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
