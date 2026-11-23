using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class IntConversionTests
{
    [Theory]
    [InlineData(1.9, 1)]
    [InlineData(-1.9, -1)]
    [InlineData(2147483648.9, 2147483648)]
    [InlineData(-2147483649.9, -2147483649)]
    [InlineData(9223372036854775808d, 9223372036854775808d)]
    [InlineData(double.MaxValue, double.MaxValue)]
    [InlineData(double.MinValue, double.MinValue)]
    [InlineData(double.Epsilon, 0)]
    public void FiniteResultsAreNotLimitedToMachineIntegers(
        double input, double expected)
    {
        Value result = Evaluate(Value.Numeric(input));
        Assert.Equal(ValueKind.Number, result.Kind);
        Assert.Equal(expected, result.Number);
    }

    [Fact]
    public void RejectsCompositeAndAbsentValuesWithoutChangingTheirContents()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted), Value.Nothing,
            Value.FromArray(array), Value.FromHashTable(table),
            Value.FromError(5), Value.FromObject(new object())];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() =>
                Evaluate(value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonfiniteValuesSuppliedThroughAnAdapter(double input)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(new Value(ValueKind.Number, input)));
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("INT(input)",
            new Dictionary<string, Value> { ["input"] = input });

    [Theory]
    [InlineData("\"1e2\"", "100")]
    [InlineData("\"1E+2\"", "100")]
    [InlineData("\".5\"", "0")]
    [InlineData("\"1.\"", "1")]
    [InlineData("\"1e-999\"", "0")]
    [InlineData("\"000000000000000000000000000000001\"", "1")]
    [InlineData("EMPTY", "0")]
    [InlineData("TRUE", "1")]
    public async Task AcceptsInvariantFiniteTextAndScalarValues(
        string input, string expected)
    {
        Assert.Equal([expected], await Run("PRINT INT(" + input + ")"));
    }

    [Theory]
    [InlineData("$FFFFFFFF")]
    [InlineData("0x10")]
    [InlineData("TRUE")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1e-")]
    [InlineData(".")]
    [InlineData("-.")]
    [InlineData("1d2")]
    [InlineData("１.９")]
    [InlineData("1,234.5")]
    [InlineData("1\u0145+2")]
    public async Task RejectsInvalidRuntimeTextAtScriptLine(string text)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("PRINT 1\nPRINT INT(\"" + text + "\")"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT INT(\"" + text
            + "\")\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task PredicateAndValFollowTheFiniteTextContract()
    {
        Assert.Equal(["0", "-999999", "-999999"], await Run(
            "PRINT CHKNUM(\"1e\")\nPRINT VAL(\"1e\")"
            + "\nPRINT VAL(\"$FFFFFFFF\")"));
    }

    [Theory]
    [InlineData("INT()")]
    [InlineData("INT(,)")]
    [InlineData("INT(,Tick())")]
    [InlineData("INT(Tick(),Tick())")]
    [InlineData("INT(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityDoesNotEvaluateArguments(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=calls\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentIsEvaluatedOnce()
    {
        Assert.Equal(["1", "1"], await Run(
            "PUBLIC calls=0\nPRINT INT(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorValidatesArityBeforeEvaluation()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("INT(1,missing)"));
        Assert.Contains("INT", error.Message);
    }

    [Theory]
    [InlineData("1\0")]
    [InlineData("1\0x")]
    public void RejectsEmbeddedNullCharacters(string text)
    {
        Assert.Throws<InvalidOperationException>(() => Evaluate(Value.String(text)));
    }
}
