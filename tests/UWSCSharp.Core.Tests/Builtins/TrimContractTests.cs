using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class TrimContractTests
{
    [Theory]
    [InlineData("TRIM(\"\")", "")]
    [InlineData("TRIM(\" a \")", "a")]
    [InlineData("TRIM(\" a b \")", "a b")]
    [InlineData("TRIM(\"　a　\")", "　a　")]
    [InlineData("TRIM(\" 　a　 \",TRUE)", "a")]
    [InlineData("TRIM(\"　a　\",FALSE)", "　a　")]
    [InlineData("TRIM(\"　a　\",0.5)", "　a　")]
    [InlineData("TRIM(\"　a　\",-0.5)", "　a　")]
    [InlineData("TRIM(\"　a　\",1.5)", "a")]
    [InlineData("TRIM(\"　a　\",-1.9)", "a")]
    [InlineData("TRIM(\"　a　\",1e+308)", "a")]
    [InlineData("TRIM(\"　a　\",-1e+308)", "a")]
    [InlineData("TRIM(\"　a　\",\" 1e0 \")", "a")]
    [InlineData("TRIM(\"　a　\",\"0.5\")", "　a　")]
    [InlineData("TRIM(\"　a　\",EMPTY)", "　a　")]
    [InlineData("TRIM(\"　a　\",)", "　a　")]
    [InlineData("TRIM(EMPTY)", "")]
    [InlineData("TRIM(123.5)", "123.5")]
    [InlineData("TRIM(TRUE)", "1")]
    [InlineData("tRiM(\" a \")", "a")]
    public void ScalarTextAndTruncatedFlagsHaveExplicitDefaults(string expression, string expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteC0RangeAndAsciiSpaceAreRemovedOnlyAtEdges(bool fullWidth)
    {
        string controls = string.Concat(Enumerable.Range(0, 33).Select(char.ConvertFromUtf32));
        Assert.Equal("center", StringOperations.Trim(controls + "center" + controls, fullWidth));
        Assert.Equal("a" + controls + "b", StringOperations.Trim("a" + controls + "b", fullWidth));
        Assert.Equal("", StringOperations.Trim(controls, fullWidth));
        Assert.Equal("!a!", StringOperations.Trim("!a!", fullWidth));
    }

    [Theory]
    [InlineData(0x7f)]
    [InlineData(0x85)]
    [InlineData(0xa0)]
    [InlineData(0x1680)]
    [InlineData(0x2003)]
    [InlineData(0x200b)]
    [InlineData(0x2028)]
    [InlineData(0x202f)]
    [InlineData(0xfeff)]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void OtherWhitespaceAndIsolatedUnitsArePreserved(int unit)
    {
        string text = (char)unit + "a" + (char)unit;
        Assert.Equal(text, StringOperations.Trim(text));
        Assert.Equal(text, StringOperations.Trim(text, fullWidth: true));
    }

    [Fact]
    public void SupplementaryAndCombiningCharactersRemainUnchanged()
    {
        const string text = "😀e\u0301";
        Assert.Equal(text, StringOperations.Trim(" \t" + text + "\r\n"));
        Assert.Equal(text, StringOperations.Trim("　" + text + "　", fullWidth: true));
        Assert.Equal("a\0b", StringOperations.Trim("\0a\0b\0"));
    }

    [Fact]
    public void FullFiniteRangeAndLongInputsHaveNoIndependentLimit()
    {
        Assert.Equal("a", Trim(Value.String("　a　"), Value.Numeric(double.MaxValue)));
        Assert.Equal("a", Trim(Value.String("　a　"), Value.Numeric(double.MinValue)));
        Assert.Equal("　a　", Trim(Value.String("　a　"), Value.Numeric(double.Epsilon)));
        Assert.Equal("-0", Trim(Value.Numeric(-0d), Value.Numeric(0)));
        string zeros = new('0', 10000);
        Assert.Equal("a", Trim(Value.String("　a　"), Value.String(zeros + "1")));
        string text = new('x', 1_000_001);
        Assert.Equal(text, Trim(Value.String(" " + text + " "), Value.Numeric(0)));
    }

    [Theory]
    [InlineData("TRIM(\"　a　\",\"$1\")")]
    [InlineData("TRIM(\"　a　\",\"1e+\")")]
    [InlineData("TRIM(\"　a　\",\"1,0\")")]
    [InlineData("TRIM(\"　a　\",\"1\"+CHR(0))")]
    [InlineData("TRIM(\"\",\"NaN\")")]
    [InlineData("TRIM(NULL)")]
    [InlineData("TRIM(NOTHING)")]
    [InlineData("TRIM(\"a\",NULL)")]
    public async Task InvalidTypesAndNumericTextAreCatchable(string expression)
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
            Assert.Throws<InvalidOperationException>(() => Trim(value, Value.Numeric(0)));
            Assert.Throws<InvalidOperationException>(() => Trim(Value.String(""), value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("TRIM()")]
    [InlineData("TRIM(,Tick())")]
    [InlineData("TRIM(Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunOnceAndErrorsKeepTheirLine()
    {
        Assert.Equal(["1", "2", "1", "2"], await Run(
            "PUBLIC calls=0\nPRINT TRIM(Tick(),Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nPRINT calls\nRESULT=calls\nFEND"));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT TRIM(\"a\",NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT TRIM(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"a\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static string Trim(Value input, Value flag) =>
        BuiltinFunctions.Evaluate("TRIM", [input, flag]).Text;
}
