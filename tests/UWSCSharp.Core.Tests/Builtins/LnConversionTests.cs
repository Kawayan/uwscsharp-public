using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class LnConversionTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, .6931471805599453)]
    [InlineData(.5, -.6931471805599453)]
    [InlineData(4, 1.3862943611198906)]
    [InlineData(2.718281828459045, 1)]
    [InlineData(1e-300, -690.7755278982137)]
    [InlineData(double.MaxValue, 709.782712893384)]
    [InlineData(double.Epsilon, -744.4400719213812)]
    public void UsesNaturalLogarithmsAcrossThePositiveDoubleRange(double input, double expected)
    {
        double result = Evaluate(Value.Numeric(input)).Number;
        Assert.InRange(Math.Abs(result - expected), 0, 1e-12);
    }

    [Fact]
    public void PowersOfTwoHaveIncreasingLogarithmsAcrossAllFiniteExponents()
    {
        double previous = double.NegativeInfinity;
        const double logarithmOfTwo = .6931471805599453;
        for (int exponent = -1074; exponent <= 1023; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            double result = Evaluate(Value.Numeric(input)).Number;
            Assert.True(double.IsFinite(result));
            Assert.True(result > previous);
            Assert.InRange(result - exponent * logarithmOfTwo, -2e-13, 2e-13);
            previous = result;
        }
    }

    [Fact]
    public void AdjacentDoublesAroundOneRetainSmallSignedResults()
    {
        Assert.Equal(-1.1102230246251565e-16,
            Evaluate(Value.Numeric(Math.BitDecrement(1))).Number);
        Assert.Equal(2.2204460492503128e-16,
            Evaluate(Value.Numeric(Math.BitIncrement(1))).Number);
        Assert.Equal(0L,
            BitConverter.DoubleToInt64Bits(Evaluate(Value.Numeric(1)).Number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-double.Epsilon)]
    [InlineData(double.MinValue)]
    public void NonpositiveValuesHaveAnExplicitDomainError(double input)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Evaluate(Value.Numeric(input)));
        Assert.Contains("LN", error.Message);
        Assert.Contains("0より大きい", error.Message);
    }

    [Fact]
    public void NegativeZeroAndEmptyAreAlsoOutsideTheDomain()
    {
        double negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Throws<InvalidOperationException>(() => Evaluate(Value.Numeric(negativeZero)));
        Assert.Throws<InvalidOperationException>(() => Evaluate(default));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonfiniteAdapterValues(double input)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(new Value(ValueKind.Number, input)));
    }

    [Fact]
    public void RejectsUnsupportedKindsWithoutChangingTheirContents()
    {
        var array = UwscArray.FromValues([Value.Numeric(-7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Evaluate(value));
        }

        Assert.Equal(Value.Numeric(-7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("2", .6931471805599453)]
    [InlineData(".5", -.6931471805599453)]
    [InlineData("1e2", 4.605170185988092)]
    [InlineData("1E+2", 4.605170185988092)]
    [InlineData("\t 1 \r\n", 0)]
    [InlineData("1.", 0)]
    [InlineData("1e-300", -690.7755278982137)]
    [InlineData("5e-324", -744.4400719213812)]
    public void ParsesInvariantFiniteText(string text, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(text)).Number);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("-1")]
    [InlineData("1e-999")]
    public void NonpositiveTextResultsAreRejectedInsteadOfClamped(string text)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Evaluate(Value.String(text)));
        Assert.Contains("0より大きい", error.Message);
    }

    [Fact]
    public void AcceptsLongTextWithoutThePredicateLengthLimit()
    {
        Assert.Equal(0, Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(Value.Numeric(Math.Log(4)),
            ExpressionEvaluator.Evaluate("LN($4)"));
    }

    [Theory]
    [InlineData("$4")]
    [InlineData("0x10")]
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
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1\0")]
    [InlineData("1\0x")]
    public void RejectsTextOutsideTheAdoptedGrammar(string text)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.String(text)));
    }

    [Theory]
    [InlineData("LN()")]
    [InlineData("LN(,)")]
    [InlineData("LN(,Tick())")]
    [InlineData("LN(Tick(),Tick())")]
    [InlineData("LN(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentSideEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=4*calls\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndNamesAreCaseInsensitive()
    {
        Assert.Equal(["1.3862943611198906", "1"], await Run(
            "PUBLIC calls=0\nPRINT lN(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=4*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("LN(1,missing)"));
        Assert.Contains("LN", error.Message);
    }

    [Theory]
    [InlineData("LN(\"$FFFFFFFF\")")]
    [InlineData("LN(0)")]
    [InlineData("LN(-1)")]
    public async Task ReportsSourceLineAndSupportsScriptExcept(string expression)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("PRINT 1\nPRINT " + expression));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT " + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateTheArgument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT LN(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("LN(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
