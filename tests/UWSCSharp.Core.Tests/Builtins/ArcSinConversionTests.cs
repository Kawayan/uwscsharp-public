using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ArcSinConversionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1.5707963267948966)]
    [InlineData(-1, -1.5707963267948966)]
    [InlineData(.5, .5235987755982989)]
    [InlineData(-.5, -.5235987755982989)]
    [InlineData(.9999999999999999, 1.5707963118937354)]
    [InlineData(-.9999999999999999, -1.5707963118937354)]
    [InlineData(double.Epsilon, double.Epsilon)]
    public void ReturnsRadianAnglesInsideTheAdoptedDomain(double input, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(input)).Number, 15);
    }

    [Fact]
    public void TinyInputsArePreservedAndTheDomainGridIsIncreasingAndOdd()
    {
        for (int exponent = -1074; exponent <= -27; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            Assert.Equal(input, Evaluate(Value.Numeric(input)).Number);
            Assert.Equal(-input, Evaluate(Value.Numeric(-input)).Number);
        }

        double previous = double.NegativeInfinity;
        for (int numerator = -1024; numerator <= 1024; numerator++)
        {
            double input = numerator / 1024d;
            double result = Evaluate(Value.Numeric(input)).Number;
            Assert.InRange(result, -Math.PI / 2, Math.PI / 2);
            Assert.True(result > previous);
            Assert.Equal(-result, Evaluate(Value.Numeric(-input)).Number);
            Assert.InRange(Math.Abs(Math.Sin(result) - input), 0, 4e-16);
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
        Assert.Contains("ARCSIN", error.Message);
        Assert.Contains("-1～1", error.Message);
    }

    [Fact]
    public void AdjacentDoublesInsideEachEndpointRemainValid()
    {
        double upper = Evaluate(Value.Numeric(Math.BitDecrement(1))).Number;
        double lower = Evaluate(Value.Numeric(Math.BitIncrement(-1))).Number;
        Assert.True(upper < Math.PI / 2);
        Assert.True(lower > -Math.PI / 2);
        Assert.Equal(-upper, lower);
    }

    [Fact]
    public void KeepsTheSignOfNegativeZero()
    {
        double input = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Equal(long.MinValue,
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
    [InlineData("1", 1.5707963267948966)]
    [InlineData("-1", -1.5707963267948966)]
    [InlineData(".5", .5235987755982989)]
    [InlineData("1e-300", 1e-300)]
    [InlineData("1E-300", 1e-300)]
    [InlineData("\t .5 \r\n", .5235987755982989)]
    [InlineData("1.", 1.5707963267948966)]
    [InlineData("1e-999", 0)]
    [InlineData("1.0000000000000001", 1.5707963267948966)]
    public void ParsesInvariantFiniteText(string text, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(text)).Number);
    }

    [Fact]
    public void NegativeTextUnderflowKeepsTheNegativeZeroResult()
    {
        Assert.Equal(long.MinValue,
            BitConverter.DoubleToInt64Bits(Evaluate(Value.String("-1e-999")).Number));
    }

    [Fact]
    public void AcceptsLongTextWithoutThePredicateLengthLimit()
    {
        Assert.Equal(Math.Asin(1), Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(0, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(Math.Asin(1)),
            ExpressionEvaluator.Evaluate("ARCSIN($1)"));
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
    [InlineData("ARCSIN()")]
    [InlineData("ARCSIN(,)")]
    [InlineData("ARCSIN(,Tick())")]
    [InlineData("ARCSIN(Tick(),Tick())")]
    [InlineData("ARCSIN(Tick(),Tick(),Tick(),Tick())")]
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
        Assert.Equal(["0.5235987755982989", "1"], await Run(
            "PUBLIC calls=0\nPRINT aRcSiN(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=.5*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("ARCSIN(1,missing)"));
        Assert.Contains("ARCSIN", error.Message);
    }

    [Theory]
    [InlineData("ARCSIN(\"$FFFFFFFF\")")]
    [InlineData("ARCSIN(2)")]
    [InlineData("ARCSIN(-2)")]
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
            new Interpreter().RunAsync("PRINT ARCSIN(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("ARCSIN(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
