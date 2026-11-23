using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class CeilConversionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(.1, 1)]
    [InlineData(-.1, 0)]
    [InlineData(1.1, 2)]
    [InlineData(-1.1, -1)]
    [InlineData(2147483647.1, 2147483648)]
    [InlineData(2147483648, 2147483648)]
    [InlineData(-2147483649, -2147483649)]
    [InlineData(9007199254740991, 9007199254740991)]
    [InlineData(9223372036854775808d, 9223372036854775808d)]
    [InlineData(double.MaxValue, double.MaxValue)]
    [InlineData(double.MinValue, double.MinValue)]
    [InlineData(double.Epsilon, 1)]
    public void RoundsUpAcrossTheFiniteDoubleRange(double input, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(input)).Number);
        Assert.Equal(expected, NumericOperations.Ceiling(input));
    }

    [Fact]
    public void ChecksAdjacentValuesAtIntegerBoundariesAndPowers()
    {
        double[] boundaries = [-2147483649, -1, 0, 1,
            2147483647, 2147483648, 9007199254740992];
        foreach (double boundary in boundaries)
        {
            double below = Math.BitDecrement(boundary);
            double above = Math.BitIncrement(boundary);
            Assert.Equal(Math.Ceiling(below), Evaluate(Value.Numeric(below)).Number);
            Assert.Equal(boundary, Evaluate(Value.Numeric(boundary)).Number);
            Assert.Equal(Math.Ceiling(above), Evaluate(Value.Numeric(above)).Number);
        }

        for (int exponent = -1074; exponent <= 1023; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            Assert.Equal(exponent < 0 ? 1 : input,
                Evaluate(Value.Numeric(input)).Number);
            Assert.Equal(exponent < 0 ? 0 : -input,
                Evaluate(Value.Numeric(-input)).Number);
        }
    }

    [Fact]
    public void KeepsTheSignOfNegativeZero()
    {
        double input = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Equal(long.MinValue,
            BitConverter.DoubleToInt64Bits(Evaluate(Value.Numeric(input)).Number));
        Assert.Equal(long.MinValue,
            BitConverter.DoubleToInt64Bits(Evaluate(Value.Numeric(-double.Epsilon)).Number));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonfiniteAdapterValues(double input)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(new Value(ValueKind.Number, input)));
        Assert.Throws<InvalidOperationException>(() =>
            NumericOperations.Ceiling(input));
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
    [InlineData("1", 1)]
    [InlineData("-1.1", -1)]
    [InlineData(".5", 1)]
    [InlineData("1e-300", 1)]
    [InlineData("1E-300", 1)]
    [InlineData("\t .5 \r\n", 1)]
    [InlineData("1.", 1)]
    [InlineData("1e-999", 0)]
    [InlineData("1e308", 1e308)]
    [InlineData("-1.7976931348623157e+308", double.MinValue)]
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
        Assert.Equal(Math.Ceiling(1d), Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(0, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(Math.Ceiling(4d)),
            ExpressionEvaluator.Evaluate("CEIL($4)"));
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
    [InlineData("CEIL()")]
    [InlineData("CEIL(,)")]
    [InlineData("CEIL(,Tick())")]
    [InlineData("CEIL(Tick(),Tick())")]
    [InlineData("CEIL(Tick(),Tick(),Tick(),Tick())")]
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
        Assert.Equal(["1", "1"], await Run(
            "PUBLIC calls=0\nPRINT cEiL(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=.5*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("CEIL(1,missing)"));
        Assert.Contains("CEIL", error.Message);
    }

    [Fact]
    public async Task ReportsSourceLineAndSupportsScriptExcept()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("PRINT 1\nPRINT CEIL(\"$FFFFFFFF\")"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT CEIL(\"$FFFFFFFF\")\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateTheArgument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT CEIL(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("CEIL(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
