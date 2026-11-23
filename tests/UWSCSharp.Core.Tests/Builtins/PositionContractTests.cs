using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class PositionContractTests
{
    [Theory]
    [InlineData("POS(\"a\",\"abc\")", 1)]
    [InlineData("POS(\"ab\",\"ABabAB\",2)", 3)]
    [InlineData("POS(\"ab\",\"ABabAB\",-1)", 5)]
    [InlineData("POS(\"aa\",\"aaaa\",2)", 2)]
    [InlineData("POS(\"aa\",\"aaaa\",-2)", 2)]
    [InlineData("POS(\"aa\",\"aaaa\",-3)", 1)]
    [InlineData("POS(\"aa\",\"aaaa\",4)", 0)]
    [InlineData("POS(\"\",\"abc\")", 0)]
    [InlineData("POS(\"a\",\"\")", 0)]
    [InlineData("POS(\"abc\",\"abc\",-2)", 0)]
    [InlineData("POS(\"b\",\"abc\",0)", 2)]
    [InlineData("POS(\"b\",\"abc\",EMPTY)", 2)]
    [InlineData("POS(\"b\",\"abc\",)", 2)]
    [InlineData("POS(\"b\",\"abc\",1.9)", 2)]
    [InlineData("POS(\"b\",\"abc\",-1.9)", 2)]
    [InlineData("POS(\"b\",\"abc\",\" 1e0 \")", 2)]
    [InlineData("POS(\"b\",\"abc\",1e+308)", 0)]
    [InlineData("POS(\"b\",\"abc\",-1e+308)", 0)]
    [InlineData("POS(\"b\",\"abc\",2147483648)", 0)]
    [InlineData("POS(\"b\",\"abc\",-2147483648)", 0)]
    [InlineData("POS(2,1232,-1)", 4)]
    [InlineData("POS(TRUE,\"TRUE1\")", 5)]
    [InlineData("POS(EMPTY,\"abc\")", 0)]
    [InlineData("POS(\"a\",EMPTY)", 0)]
    [InlineData("POS(\"B\",\"A😀B\")", 3)]
    [InlineData("POS(CHR(0),\"a\"+CHR(0)+\"b\")", 2)]
    [InlineData("POS(\"σ\",\"ς\")", 1)]
    [InlineData("POS(\"i\",\"İ\")", 0)]
    [InlineData("POS(\"SS\",\"ß\")", 0)]
    [InlineData("POS(\"é\",\"e\"+CHR($301))", 0)]
    [InlineData("pOs(\"A\",\"Ａ\")", 0)]
    public void SearchesOverlapsAndFiniteOccurrenceBoundaries(string expression, int expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Number);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void PairedSurrogateHalvesNeverProduceInvalidPositions(int occurrence)
    {
        Assert.Equal(0, StringOperations.Position("\udc00", "\ud800\udc00", occurrence));
        Assert.Equal(0, StringOperations.Position("\ud800", "\ud800\udc00", occurrence));
        Assert.Equal(2, StringOperations.Position("\udc00", "\ud800\udc00\udc00", occurrence));
        Assert.Equal(1, StringOperations.Position("\udc00", "\udc00\ud800\udc00", occurrence));
        Assert.Equal(0, StringOperations.Position("a\ud800", "a\ud800\udc00", occurrence));
        Assert.Equal(0, StringOperations.Position("\udc00b", "\ud800\udc00b", occurrence));
        Assert.Equal(2, StringOperations.Position("\ud800\udc00", "a\ud800\udc00b", occurrence));
    }

    [Fact]
    public void InvalidBoundaryMatchesDoNotConsumeOccurrence()
    {
        const string source = "\udc00\ud800\udc00\udc00";
        Assert.Equal(3, StringOperations.Position("\udc00", source, 2));
        Assert.Equal(1, StringOperations.Position("\udc00", source, -2));
        Assert.Equal(0, StringOperations.Position("\udc00", source, 3));
        Assert.Equal(0, StringOperations.Position("\udc00", source, -3));
    }

    [Fact]
    public void FullFiniteRangeAndLongInputsDoNotUseIndependentLimits()
    {
        Assert.Equal(0, Position(Value.String("a"), Value.String("a"), Value.Numeric(double.MaxValue)));
        Assert.Equal(0, Position(Value.String("a"), Value.String("a"), Value.Numeric(double.MinValue)));
        string zeros = new('0', 10000);
        Assert.Equal(2, Position(Value.String("a"), Value.String("aaa"), Value.String(zeros + "2")));
        string text = new('x', 1_000_001);
        Assert.Equal(text.Length, StringOperations.Position("x", text, -1));
        Assert.Equal(0, StringOperations.Position("a", text, int.MinValue));
    }

    [Theory]
    [InlineData("", "1", "4", "1")]
    [InlineData("OPTION SAMESTR\n", "2", "3", "0")]
    public async Task SameStringControlsCaseWithoutLeaking(string option,
        string forward, string reverse, string sigma)
    {
        Assert.Equal([forward, reverse, sigma], await Run(option
            + "PRINT POS(\"a\",\"Aa\")\nPRINT POS(\"a\",\"aAaA\",-1)\nPRINT POS(\"σ\",\"ς\")"));
        Assert.Equal(["1"], await Run("PRINT POS(\"a\",\"Aa\")"));
    }

    [Theory]
    [InlineData("POS(\"a\",\"a\",\"$1\")")]
    [InlineData("POS(\"a\",\"a\",\"1e+\")")]
    [InlineData("POS(\"a\",\"a\",\"1,0\")")]
    [InlineData("POS(\"a\",\"a\",\"1\"+CHR(0))")]
    [InlineData("POS(\"\",\"\",\"NaN\")")]
    [InlineData("POS(NULL,\"abc\")")]
    [InlineData("POS(\"a\",NULL)")]
    public async Task InvalidArgumentsAreCatchable(string expression)
    {
        Assert.Equal(["caught"], await Run("TRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public void InvalidKindsAndNonfiniteValuesDoNotMutateInputs()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [Value.Nothing, Value.FromError(5), new(ValueKind.Null),
            Value.FromObject(new object()), Value.FromArray(array), Value.FromHashTable(table),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Position(value, Value.String("a"), Value.Numeric(1)));
            Assert.Throws<InvalidOperationException>(() => Position(Value.String("a"), value, Value.Numeric(1)));
            Assert.Throws<InvalidOperationException>(() => Position(Value.String("a"), Value.String("a"), value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("POS()")]
    [InlineData("POS(Tick())")]
    [InlineData("POS(,Tick())")]
    [InlineData("POS(Tick(),)")]
    [InlineData("POS(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunInOrderOnceAndErrorsKeepTheirLine()
    {
        Assert.Equal(["1", "2", "3", "0", "3"], await Run(
            "PUBLIC calls=0\nPRINT POS(Tick(),Tick(),Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nPRINT calls\nRESULT=calls\nFEND"));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT POS(NULL,\"a\")"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT POS(Tick(),\"a\")\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"a\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static int Position(Value needle, Value source, Value occurrence) =>
        (int)BuiltinFunctions.Evaluate("POS", [needle, source, occurrence]).Number;
}
