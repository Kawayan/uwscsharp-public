using System.Globalization;
using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SelectCompletionTests
{
    [Theory]
    [InlineData("\"02\"", "\"2\"", "default")]
    [InlineData("\"02\"", "2", "match")]
    [InlineData("EMPTY", "NULL", "default")]
    [InlineData("NULL", "EMPTY", "default")]
    [InlineData("NOTHING", "NOTHING", "match")]
    [InlineData("NOTHING", "NULL", "default")]
    [InlineData("0", "NOTHING", "default")]
    [InlineData("\"a,b\"", "\"a,b\"", "match")]
    [InlineData("\"Ａ\"", "\"A\"", "default")]
    [InlineData("\"あ\"", "\"ア\"", "default")]
    public async Task SpecialValuesAndQuotedCommasUseAdoptedComparison(
        string selector, string candidate, string expected)
    {
        Assert.Equal([expected], await Run("SELECT " + selector
            + "\nCASE " + candidate
            + "\nPRINT \"match\"\nDEFAULT\nPRINT \"default\"\nSELEND"));
    }

    [Fact]
    public async Task EmptyFirstMatchDoesNotEvaluateLaterCandidates()
    {
        Assert.Empty(await Run(
            "SELECT 1\nCASE 1\nCASE INT(NULL)\nPRINT 9\nSELEND"));
    }

    [Fact]
    public async Task EmptySelectStillEvaluatesSelectorOnce()
    {
        Assert.Equal(["selector"], await Run("""
            SELECT Tick()
            SELEND
            FUNCTION Tick()
                PRINT "selector"
                RESULT=1
            FEND
            """));
    }

    [Fact]
    public async Task SelectorIsSnapshotAndCandidatesEvaluateLeftToRight()
    {
        Assert.Equal(["1", "2", "3", "default"], await Run("""
            PUBLIC n=0
            SELECT Tick()
            CASE Tick(),Tick()
                PRINT "match"
            DEFAULT
                PRINT "default"
            SELEND
            FUNCTION Tick()
                n=n+1
                PRINT n
                RESULT=n
            FEND
            """));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task StringAndNumericComparisonDoNotUseCurrentCulture(string name)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            Assert.Equal(["match"], await Run(
                "SELECT 0.5\nCASE \".5\"\nPRINT \"match\"\nSELEND"));
            Assert.Equal(["match"], await Run(
                "SELECT \"I\"\nCASE \"i\"\nPRINT \"match\"\nSELEND"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("SELECT\nSELEND", 1)]
    [InlineData("SELECT 1", 1)]
    [InlineData("SELECT 1\nCASE\nSELEND", 2)]
    [InlineData("SELECT 1\nCASE 1,\nSELEND", 2)]
    [InlineData("SELECT 1\nCASE ,1\nSELEND", 2)]
    [InlineData("SELECT 1\nDEFAULT\nDEFAULT\nSELEND", 3)]
    [InlineData("SELECT 1\nDEFAULT\nCASE 1\nSELEND", 3)]
    [InlineData("SELECT 1\nDEFAULT 1\nSELEND", 2)]
    [InlineData("SELECT 1\nSELEND 1", 2)]
    [InlineData("SELECT 1\nPRINT 1\nCASE 1\nSELEND", 2)]
    [InlineData("SELECT 1\nCASE 1\nCASE 2\nPRINT 1+\nSELEND", 4)]
    public void InvalidStructureIsRejectedBeforeExecution(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile(source));
        Assert.Equal(line, error.Line);
    }

    [Theory]
    [InlineData(127, true)]
    [InlineData(128, false)]
    public void NestedCasesUseCommonDepthLimit(int depth, bool accepted)
    {
        string source = string.Concat(Enumerable.Repeat("SELECT 1\nCASE 1\n", depth))
            + string.Concat(Enumerable.Repeat("SELEND\n", depth));
        if (accepted)
        {
            ScriptCompiler.Compile(source);
        }
        else
        {
            Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
        }
    }

    [Fact]
    public async Task NestedDefaultAndConstUseTheirExistingScopes()
    {
        Assert.Equal(["inner", "7"], await Run("""
            SELECT 1
            CASE 1
                SELECT 2
                CASE 1
                    CONST c=ABS(-7)
                DEFAULT
                    PRINT "inner"
                SELEND
            DEFAULT
                PRINT "outer"
            SELEND
            PRINT c
            """));
    }

    [Fact]
    public async Task RoutineExitPreservesResultAndFinally()
    {
        Assert.Equal(["cleanup", "7"], await Run("""
            PRINT F()
            FUNCTION F()
                TRY
                    SELECT 1
                    CASE 1
                        RESULT=7
                        EXIT
                    SELEND
                    PRINT INT(NULL)
                FINALLY
                    PRINT "cleanup"
                ENDTRY
            FEND
            """));
    }

    [Theory]
    [InlineData("SELECT Bad()\nSELEND", 4)]
    [InlineData("SELECT 1\nCASE Bad()\nSELEND", 5)]
    public async Task CalleeFailureKeepsItsOwnLine(string body, int line)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run(body + "\nFUNCTION Bad()\nRESULT=INT(NULL)\nFEND"));
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public async Task ObjectComparisonUsesReferenceIdentityAndDisposesSession()
    {
        object same = new object();
        foreach (object candidate in new[] { same, new object() })
        {
            var adapter = new SelectAdapter(Value.FromObject(same), Value.FromObject(candidate));
            var output = new List<string>();
            await new Interpreter(windows: adapter).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\nSELECT o.Selector\nCASE o.Candidate"
                + "\nPRINT 1\nDEFAULT\nPRINT 0\nSELEND", output.Add);
            Assert.Equal([ReferenceEquals(same, candidate) ? "1" : "0"], output);
            Assert.Equal(1, adapter.Disposals);
        }
    }

    [Fact]
    public async Task UnsupportedComparisonValuesFailAtCaseAndDisposeSession()
    {
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted),
            Value.FromError(5), Value.FromArray(UwscArray.FromValues([Value.Numeric(1)])),
            Value.FromHashTable(UwscHashTable.Create()),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity)];
        foreach (Value candidate in invalid)
        {
            var adapter = new SelectAdapter(Value.Numeric(1), candidate);
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter(windows: adapter).RunAsync(
                    "o=CREATEOLEOBJ(\"fixture\")\nSELECT o.Selector\nCASE o.Candidate\nSELEND",
                    _ => { }));
            Assert.Equal(3, error.Line);
            Assert.Equal(1, adapter.Disposals);
        }
    }

    [Theory]
    [InlineData("Selector")]
    [InlineData("Candidate")]
    public async Task CancellationOnAdapterReturnExecutesNoBody(string member)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new SelectAdapter(Value.Numeric(1), Value.Numeric(1))
        {
            OnMember = name =>
            {
                if (name == member)
                {
                    cancellation.Cancel();
                }
            }
        };
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\nSELECT o.Selector\nCASE o.Candidate"
                + "\nPRINT 1\nDEFAULT\nPRINT 0\nSELEND", output.Add, cancellation.Token));
        Assert.Empty(output);
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData(1, "１番目が選ばれました")]
    [InlineData(2, "２番目が選ばれました")]
    [InlineData(4, "３番目が選ばれました")]
    [InlineData(0, "タイムアウトしました")]
    public async Task HelpButtonChoiceBranchesRecordDisplayAndClear(
        int choice, string expected)
    {
        var host = new MouseMoveExampleHost();
        await new Interpreter().RunAsync("DIM choice=" + choice + "\n" + """
            SELECT choice
            CASE SLCT_1
                DisplayFixture("１番目が選ばれました")
            CASE SLCT_2
                DisplayFixture("２番目が選ばれました")
            CASE SLCT_3
                DisplayFixture("３番目が選ばれました")
            CASE 0
                DisplayFixture("タイムアウトしました")
            SELEND
            Sleep(3)
            DisplayFixture()
            PROCEDURE DisplayFixture(text="")
                PRINT text
            FEND
            """, host);
        Assert.Equal([expected, ""], host.Output);
        Assert.Equal([TimeSpan.FromSeconds(3)], host.Delays);
    }

    [Theory]
    [InlineData("AAA", "AAA")]
    [InlineData("aaa", "AAA")]
    [InlineData("BBB", "BBB")]
    [InlineData("3", "３")]
    public async Task HelpInputChoiceUsesStringVariableAndNumericCases(
        string input, string expected)
    {
        Assert.Equal([expected], await Run("DIM inputValue=\"" + input + "\"\n" + """
            wkmoji="BBB"
            SELECT inputValue
            CASE "AAA"
                DisplayFixture("AAA")
            CASE wkmoji
                DisplayFixture(wkmoji)
            CASE 3
                DisplayFixture("３")
            DEFAULT
                DisplayFixture("訳わかんないです")
            SELEND
            PROCEDURE DisplayFixture(text)
                PRINT text
            FEND
            """));
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("")]
    public async Task HelpInputChoiceReportsInvalidMixedNumericComparison(string input)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("DIM inputValue=\"" + input + "\"\n"
                + "wkmoji=\"BBB\"\nSELECT inputValue\nCASE \"AAA\"\nPRINT 1\n"
                + "CASE wkmoji\nPRINT 2\nCASE 3\nPRINT 3\nDEFAULT\nPRINT 4\nSELEND"));
        Assert.Equal(8, error.Line);
    }

    private sealed class SelectAdapter(Value selector, Value candidate)
        : IWindowsAutomation, IWindowsAutomationSession
    {
        internal Action<string>? OnMember
        {
            get; init;
        }

        internal int Disposals
        {
            get; private set;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(Value.FromObject(this));

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken token)
        {
            OnMember?.Invoke(name);
            return ValueTask.FromResult(name == "Selector" ? selector : candidate);
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
