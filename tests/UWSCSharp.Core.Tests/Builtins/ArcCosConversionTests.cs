using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ArcCosConversionTests
{
    [Theory]
    [InlineData(0, 1.5707963267948966)]
    [InlineData(1, 0)]
    [InlineData(-1, 3.141592653589793)]
    [InlineData(.5, 1.0471975511965979)]
    [InlineData(-.5, 2.0943951023931957)]
    [InlineData(.9999999999999999, 1.4901161193847656e-8)]
    [InlineData(-.9999999999999999, 3.141592638688632)]
    [InlineData(double.Epsilon, 1.5707963267948966)]
    public void ReturnsRadianAnglesInsideTheAdoptedDomain(double input, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(input)).Number, 15);
    }

    [Fact]
    public void TinyInputsRoundToHalfPiAndTheDomainGridIsDecreasing()
    {
        for (int exponent = -1074; exponent <= -56; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            Assert.Equal(Math.PI / 2, Evaluate(Value.Numeric(input)).Number);
            Assert.Equal(Math.PI / 2, Evaluate(Value.Numeric(-input)).Number);
        }

        double previous = double.PositiveInfinity;
        for (int numerator = -1024; numerator <= 1024; numerator++)
        {
            double input = numerator / 1024d;
            double result = Evaluate(Value.Numeric(input)).Number;
            double complement = Evaluate(Value.Numeric(-input)).Number;
            Assert.InRange(result, 0, Math.PI);
            Assert.True(result < previous);
            Assert.InRange(Math.Abs(result + complement - Math.PI), 0, 1e-15);
            Assert.InRange(Math.Abs(Math.Cos(result) - input), 0, 6e-16);
            previous = result;
        }
    }

    [Theory]
    [InlineData(1.0000000000000002)]
    [InlineData(-1.0000000000000002)]
    [InlineData(2)]
    [InlineData(-2)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void InputsOutsideTheClosedIntervalHaveAnExplicitDomainError(double input)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.Numeric(input)));
        Assert.Contains("ARCCOS", error.Message);
        Assert.Contains("-1～1", error.Message);
    }

    [Fact]
    public void AdjacentDoublesInsideEachEndpointRemainDistinctFromTheEndpoints()
    {
        double upper = Evaluate(Value.Numeric(Math.BitDecrement(1))).Number;
        double lower = Evaluate(Value.Numeric(Math.BitIncrement(-1))).Number;
        Assert.Equal(1.4901161193847656e-8, upper);
        Assert.True(lower < Math.PI);
        Assert.True(lower > Math.PI / 2);
    }

    [Fact]
    public void BothSignsOfZeroReturnHalfPiAndTheUpperEndpointReturnsPositiveZero()
    {
        double input = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Equal(Math.PI / 2, Evaluate(Value.Numeric(input)).Number);
        Assert.Equal(Math.PI / 2, Evaluate(Value.Numeric(0)).Number);
        Assert.Equal(0L,
            BitConverter.DoubleToInt64Bits(Evaluate(Value.Numeric(1)).Number));
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
    [InlineData("1", 0)]
    [InlineData("-1", 3.141592653589793)]
    [InlineData(".5", 1.0471975511965979)]
    [InlineData("1e-300", 1.5707963267948966)]
    [InlineData("1E-300", 1.5707963267948966)]
    [InlineData("\t .5 \r\n", 1.0471975511965979)]
    [InlineData("1.", 0)]
    [InlineData("1e-999", 1.5707963267948966)]
    [InlineData("1.0000000000000001", 0)]
    public void ParsesInvariantFiniteText(string text, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(text)).Number);
    }

    [Fact]
    public void NegativeTextUnderflowAlsoReturnsHalfPi()
    {
        Assert.Equal(Math.PI / 2, Evaluate(Value.String("-1e-999")).Number);
    }

    [Fact]
    public void AcceptsLongTextWithoutThePredicateLengthLimit()
    {
        Assert.Equal(Math.Acos(1), Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(Math.PI / 2, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(Math.Acos(1)),
            ExpressionEvaluator.Evaluate("ARCCOS($1)"));
    }

    [Theory]
    [InlineData("$4")]
    [InlineData("$1")]
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
    [InlineData("ARCCOS()")]
    [InlineData("ARCCOS(,)")]
    [InlineData("ARCCOS(,Tick())")]
    [InlineData("ARCCOS(Tick(),Tick())")]
    [InlineData("ARCCOS(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentSideEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=.5*calls\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndNamesAreCaseInsensitive()
    {
        Assert.Equal(["1.0471975511965979", "1"], await Run(
            "PUBLIC calls=0\nPRINT aRcCoS(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=.5*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("ARCCOS(1,missing)"));
        Assert.Contains("ARCCOS", error.Message);
    }

    [Theory]
    [InlineData("ARCCOS(\"$FFFFFFFF\")")]
    [InlineData("ARCCOS(2)")]
    [InlineData("ARCCOS(-2)")]
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
            new Interpreter().RunAsync("PRINT ARCCOS(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("ARCCOS(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
