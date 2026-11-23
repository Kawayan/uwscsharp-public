using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ExpConversionTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2.718281828459045)]
    [InlineData(-1, .36787944117144233)]
    [InlineData(.6931471805599453, 2)]
    [InlineData(-.6931471805599453, .5)]
    [InlineData(4, 54.598150033144236)]
    [InlineData(100, 2.6881171418161356e43)]
    [InlineData(double.Epsilon, 1)]
    public void UsesTheFiniteDoubleExponentialContract(double input, double expected)
    {
        double result = Evaluate(Value.Numeric(input)).Number;
        Assert.InRange(result / expected, 1 - 2e-15, 1 + 2e-15);
    }

    [Fact]
    public void TinyInputsRoundToOneAndResultsAreNondecreasing()
    {
        for (int exponent = -1074; exponent <= -55; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            Assert.Equal(1, Evaluate(Value.Numeric(input)).Number);
            Assert.Equal(1, Evaluate(Value.Numeric(-input)).Number);
        }

        double previous = 0;
        for (int input = -745; input <= 709; input++)
        {
            double result = Evaluate(Value.Numeric(input)).Number;
            Assert.True(double.IsFinite(result));
            Assert.True(result > 0);
            Assert.True(result >= previous);
            previous = result;
        }
    }

    [Fact]
    public void BothSignsOfZeroReturnPositiveOne()
    {
        double input = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Equal(1, Evaluate(Value.Numeric(input)).Number);
        Assert.Equal(1, Evaluate(Value.Numeric(0)).Number);
    }

    [Fact]
    public void AdjacentDoublesSeparateTheFiniteAndOverflowResults()
    {
        double boundary = 709.782712893384;
        double result = Evaluate(Value.Numeric(boundary)).Number;
        Assert.InRange(result, 1e308, double.MaxValue);
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.Numeric(Math.BitIncrement(boundary))));
    }

    [Theory]
    [InlineData(710)]
    [InlineData(1e308)]
    [InlineData(double.MaxValue)]
    public void RejectsOverflowRatherThanReturningInfinity(double input)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.Numeric(input)));
    }

    [Fact]
    public void KeepsSubnormalResultsAndRoundsUnderflowToPositiveZero()
    {
        double boundary = -745.1332191019411;
        Assert.Equal(2 * double.Epsilon,
            Evaluate(Value.Numeric(-744)).Number);
        Assert.Equal(double.Epsilon,
            Evaluate(Value.Numeric(boundary)).Number);
        double zero = Evaluate(Value.Numeric(Math.BitDecrement(boundary))).Number;
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(zero));
    }

    [Theory]
    [InlineData(-746)]
    [InlineData(-1e308)]
    [InlineData(double.MinValue)]
    public void LargeNegativeFiniteInputsUnderflowToPositiveZero(double input)
    {
        Assert.Equal(0L,
            BitConverter.DoubleToInt64Bits(Evaluate(Value.Numeric(input)).Number));
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
    [InlineData("1", 2.718281828459045)]
    [InlineData("-1", .36787944117144233)]
    [InlineData("1e-300", 1)]
    [InlineData("1E-300", 1)]
    [InlineData("\t 1 \r\n", 2.718281828459045)]
    [InlineData("1.", 2.718281828459045)]
    [InlineData("1e-999", 1)]
    public void ParsesInvariantFiniteText(string text, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(text)).Number);
    }

    [Fact]
    public void AcceptsLongTextWithoutThePredicateLengthLimit()
    {
        Assert.Equal(Math.Exp(1), Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(1, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(Math.Exp(4)),
            ExpressionEvaluator.Evaluate("EXP($4)"));
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
    [InlineData("EXP()")]
    [InlineData("EXP(,)")]
    [InlineData("EXP(,Tick())")]
    [InlineData("EXP(Tick(),Tick())")]
    [InlineData("EXP(Tick(),Tick(),Tick(),Tick())")]
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
        Assert.Equal(["54.598150033144236", "1"], await Run(
            "PUBLIC calls=0\nPRINT eXp(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=4*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("EXP(1,missing)"));
        Assert.Contains("EXP", error.Message);
    }

    [Theory]
    [InlineData("EXP(\"$FFFFFFFF\")")]
    [InlineData("EXP(710)")]
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
            new Interpreter().RunAsync("PRINT EXP(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("EXP(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
