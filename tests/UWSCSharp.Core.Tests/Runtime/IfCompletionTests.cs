using System.Globalization;
using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class IfCompletionTests
{
    [Theory]
    [InlineData("IF o.Then THEN PRINT \"yes\"", "yes")]
    [InlineData("IF o . THEN THEN PRINT \"yes\"", "yes")]
    [InlineData("IF TRUE THEN PRINT o.Else ELSE PRINT \"no\"", "member")]
    [InlineData("IF TRUE THEN PRINT o . ELSE ELSE PRINT \"no\"", "member")]
    [InlineData("IFB o.Then\nPRINT \"yes\"\nENDIF", "yes")]
    [InlineData("FOR i=o.To TO 1\nPRINT i\nNEXT", "1")]
    [InlineData("FOR i=1 TO o.Step STEP 1\nPRINT i\nNEXT", "1")]
    [InlineData("IF o.Child1.Then THEN PRINT \"yes\"", "yes")]
    [InlineData("IF (o).Then THEN PRINT \"yes\"", "yes")]
    [InlineData("IF o.Then() THEN PRINT o.Else()", "member")]
    [InlineData("DIM items[]=o\nIF items[0].Then THEN PRINT \"yes\"", "yes")]
    public async Task MemberNamesAreNotStatementDelimiters(string body, string expected)
    {
        var output = new List<string>();
        var adapter = new KeywordMembers();
        await new Interpreter(windows: adapter).RunAsync(
            "o=CREATEOLEOBJ(\"fixture\")\n" + body, output.Add);
        Assert.Equal([expected], output);
        Assert.Equal(1, adapter.Disposals);
    }

    [Theory]
    [InlineData("1.")]
    [InlineData("+1.")]
    [InlineData("-1.")]
    [InlineData(".5")]
    [InlineData("1.0")]
    [InlineData("1e2")]
    public async Task NumericDotRemainsPartOfLiteralBeforeThen(string condition)
    {
        Assert.Equal(["yes"], await Run("IF " + condition + " THEN PRINT \"yes\""));
    }

    [Theory]
    [InlineData("0", "no")]
    [InlineData("-0", "no")]
    [InlineData("FALSE", "no")]
    [InlineData("EMPTY", "no")]
    [InlineData("TRUE", "yes")]
    [InlineData("-0.5", "yes")]
    [InlineData("1e-300", "yes")]
    [InlineData("1e300", "yes")]
    [InlineData("\"0\"", "no")]
    [InlineData("\"1e2\"", "yes")]
    [InlineData("\"$FF\"", "yes")]
    public async Task ConditionUsesExistingFiniteZeroComparison(string condition, string expected)
    {
        Assert.Equal([expected], await Run(
            "IF " + condition + " THEN PRINT \"yes\" ELSE PRINT \"no\""));
    }

    [Fact]
    public async Task ConditionRunsOnceAndUnchosenBranchHasNoEffect()
    {
        Assert.Equal(["yes", "1", "no"], await Run("""
            PUBLIC calls=0
            IF Tick() THEN PRINT "yes" ELSE PRINT INT(NULL)
            PRINT calls
            IF FALSE THEN PRINT INT(NULL) ELSE PRINT "no"
            FUNCTION Tick()
                calls=calls+1
                RESULT=1
            FEND
            """));
    }

    [Theory]
    [InlineData("THEN ELSE")]
    [InlineData("o.Else")]
    [InlineData("ELSE,THEN")]
    public async Task KeywordsInQuotedBranchAndArgumentsAreData(string text)
    {
        Assert.Equal([text], await Run("IF POS(\"THEN\",\"THEN ELSE\")"
            + " THEN PRINT \"" + text + "\" ELSE PRINT \"bad\""));
    }

    [Theory]
    [InlineData("TRUE", "1", "11")]
    [InlineData("FALSE", "", "10")]
    public async Task SemicolonStartsIndependentStatement(string condition,
        string first, string final)
    {
        string[] output = await Run("n=0;IF " + condition
            + " THEN n=1;n=n+10;PRINT n");
        Assert.Equal([final], output);
        string[] expected = first.Length == 0 ? [] : [first];
        Assert.Equal(expected,
            await Run("IF " + condition + " THEN PRINT 1"));
    }

    [Theory]
    [InlineData("BREAK", "1")]
    [InlineData("CONTINUE", "2")]
    public async Task InlineFlowKeepsItsEnclosingLoop(string instruction, string count)
    {
        Assert.Equal([count], await Run("n=0\nFOR i=1 TO 3\nIF i=2 THEN "
            + instruction + "\nn=n+1\nNEXT\nPRINT n"));
    }

    [Fact]
    public async Task InlineExitAndAssignmentUseRoutineAndFinallyContracts()
    {
        Assert.Equal(["cleanup", "9"], await Run("""
            PRINT F()
            FUNCTION F()
                TRY
                    RESULT=9
                    IF TRUE THEN EXIT
                    PRINT INT(NULL)
                FINALLY
                    PRINT "cleanup"
                ENDTRY
            FEND
            """));
    }

    [Theory]
    [InlineData("IF THEN PRINT 1")]
    [InlineData("IF TRUE THEN PRINT 1 ELSE")]
    [InlineData("IF FALSE THEN PRINT 1+")]
    [InlineData("IF TRUE THEN IF TRUE THEN PRINT 1")]
    [InlineData("IF FALSE THEN PRINT 1 ELSE IF TRUE THEN PRINT 2")]
    public void MalformedAndNestedInlineFormsAreRejectedBeforeExecution(string source)
    {
        Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
    }

    [Fact]
    public async Task UnsupportedKindsAndNonfiniteAdapterResultsFailAtIfLine()
    {
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(UwscArray.FromValues([Value.Numeric(1)])),
            Value.FromHashTable(UwscHashTable.Create()),
            Value.String("TRUE"), Value.String("bad"),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            var adapter = new KeywordMembers { ConditionValue = value };
            var output = new List<string>();
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter(windows: adapter).RunAsync(
                    "o=CREATEOLEOBJ(\"fixture\")\nIF o.Then THEN PRINT 1 ELSE PRINT 2", output.Add));
            Assert.Equal(2, error.Line);
            Assert.Empty(output);
            Assert.Equal(1, adapter.Disposals);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CancellationOnConditionReturnExecutesNeitherBranch(int truth)
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new KeywordMembers
        {
            ConditionValue = Value.Numeric(truth),
            OnMember = cancellation.Cancel
        };
        var output = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter(windows: adapter).RunAsync(
                "o=CREATEOLEOBJ(\"fixture\")\nIF o.Then THEN PRINT 1 ELSE PRINT 2",
                output.Add, cancellation.Token));
        Assert.Empty(output);
        Assert.Equal(1, adapter.Disposals);
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
            Assert.Equal(["yes"], await Run("IF \".5\" THEN PRINT \"yes\""));
            await Assert.ThrowsAsync<ScriptException>(() => Run("IF \"0,5\" THEN PRINT 1"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task ExistingNumericTextLengthBoundariesRemainExplicit()
    {
        foreach (string text in new[] { new string('0', 31) + "1", ".5" + new string('0', 62) })
        {
            Assert.Equal(["yes"], await Run("IF \"" + text + "\" THEN PRINT \"yes\""));
        }

        foreach (string text in new[] { new string('0', 32) + "1", ".5" + new string('0', 63) })
        {
            await Assert.ThrowsAsync<ScriptException>(() => Run("IF \"" + text + "\" THEN PRINT 1"));
        }
    }

    [Theory]
    [InlineData("IF NULL THEN PRINT 1", 1)]
    [InlineData("IF TRUE THEN PRINT INT(NULL)", 1)]
    public async Task ConditionAndSelectedBodyKeepTheirSourceLine(string source, int line)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => Run(source));
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public async Task ConditionCalleeErrorKeepsCalleeLine()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("IF Bad() THEN PRINT 1\nFUNCTION Bad()\nRESULT=INT(NULL)\nFEND"));
        Assert.Equal(3, error.Line);
    }

    private sealed class KeywordMembers : IWindowsAutomation, IWindowsAutomationSession
    {
        internal int Disposals;

        internal Value? ConditionValue
        {
            get; init;
        }

        internal Action? OnMember
        {
            get; init;
        }

        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token) =>
            ValueTask.FromResult(Value.FromObject(this));

        public ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken token)
        {
            OnMember?.Invoke();
            return ValueTask.FromResult(name.Equals("Else", StringComparison.OrdinalIgnoreCase)
                ? Value.String("member") : name == "Child1" ? Value.FromObject(this)
                : ConditionValue ?? Value.Numeric(1));
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }
}
