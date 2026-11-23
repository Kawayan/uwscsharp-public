using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class RoundConversionTests
{
    [Theory]
    [InlineData(2.5, 0, 2)]
    [InlineData(3.5, 0, 4)]
    [InlineData(-2.5, 0, -2)]
    [InlineData(-3.5, 0, -4)]
    [InlineData(250, 2, 200)]
    [InlineData(350, 2, 400)]
    [InlineData(-250, 2, -200)]
    [InlineData(1.25, -1, 1.2)]
    [InlineData(1.35, -1, 1.4)]
    [InlineData(2.675, -2, 2.68)]
    [InlineData(2.685, -2, 2.68)]
    [InlineData(1.9e-16, -16, 2e-16)]
    [InlineData(1, 20, 0)]
    [InlineData(1, -20, 1)]
    public void UsesAdoptedDoubleRounding(double input, int digits, double expected)
    {
        Assert.Equal(expected, NumericOperations.Round(input, digits));
    }

    [Fact]
    public void LargeFiniteValuesDoNotOverflowAtAnySupportedDigit()
    {
        double[] values = [1e50, -1e50, 1e308, -1e308,
            double.MaxValue, double.MinValue];
        foreach (double input in values)
        {
            for (int digits = -20; digits <= 20; digits++)
            {
                Value result = Evaluate(Value.Numeric(input), Value.Numeric(digits));
                Assert.Equal(BitConverter.DoubleToInt64Bits(input),
                    BitConverter.DoubleToInt64Bits(result.Number));
            }
        }
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(-16)]
    [InlineData(0)]
    [InlineData(20)]
    public void TinyNegativeValuesRetainTheSignOfZero(int digits)
    {
        double result = NumericOperations.Round(-double.Epsilon, digits);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(result));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonfiniteValuesAtBothArgumentsAndThePublicApi(double input)
    {
        Value invalid = new(ValueKind.Number, input);
        Assert.Throws<InvalidOperationException>(() => Evaluate(invalid, default));
        Assert.Throws<InvalidOperationException>(() => Evaluate(default, invalid));
        Assert.Throws<InvalidOperationException>(() => NumericOperations.Round(input, 0));
    }

    [Fact]
    public void RejectsUnsupportedKindsAtBothArgumentsWithoutChangingThem()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), Value.Nothing,
            Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Evaluate(value, default));
            Assert.Throws<InvalidOperationException>(() => Evaluate(default, value));
        }

        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(new Value(ValueKind.Omitted), default));
        Assert.Equal(0, Evaluate(default, new Value(ValueKind.Omitted)).Number);
        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("1e2", "-1.9", 100)]
    [InlineData("\t 3.5 \r\n", "0", 4)]
    [InlineData("000000000000000000000000000000001", "-20", 1)]
    [InlineData("1e-999", "20.9", 0)]
    [InlineData("1", "-20.9", 1)]
    public void ConvertsBothArgumentsAndTruncatesDigits(
        string input, string digits, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(input), Value.String(digits)).Number);
    }

    [Theory]
    [InlineData("$FFFFFFFF")]
    [InlineData("0x10")]
    [InlineData("1e")]
    [InlineData("1d2")]
    [InlineData("１.９")]
    [InlineData("1,234.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1\0x")]
    [InlineData("1\u0145+2")]
    public void RejectsInvalidTextAtBothArguments(string text)
    {
        Assert.Throws<InvalidOperationException>(() => Evaluate(Value.String(text), default));
        Assert.Throws<InvalidOperationException>(() => Evaluate(default, Value.String(text)));
    }

    [Theory]
    [InlineData(-21)]
    [InlineData(21)]
    [InlineData(2147483648d)]
    [InlineData(double.MaxValue)]
    public void RejectsOutOfRangeTruncatedDigits(double digits)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(default, Value.Numeric(digits)));
    }

    [Theory]
    [InlineData("ROUND()")]
    [InlineData("ROUND(,)")]
    [InlineData("ROUND(,Tick())")]
    [InlineData("ROUND(Tick(),Tick(),Tick())")]
    [InlineData("ROUND(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentSideEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=calls\nFEND"));
    }

    [Fact]
    public async Task EvaluatesArgumentsOnceFromLeftToRightBeforeConversion()
    {
        Assert.Equal(["1", "2", "0", "2"], await Run(
            "PUBLIC calls=0\nPRINT rOuNd(Tick(),Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nPRINT calls\nRESULT=calls\nFEND"));
        Assert.Equal(["caught", "1"], await Run(
            "PUBLIC calls=0\nTRY\nPRINT ROUND(\"bad\",Tick())\n"
            + "EXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=0\nFEND"));
    }

    [Fact]
    public async Task OmittedOrEmptyDigitsDefaultToZero()
    {
        Assert.Equal(["2", "2", "2", "0"], await Run(
            "PRINT ROUND(2.5)\nPRINT ROUND(2.5,)\n"
            + "PRINT ROUND(2.5,EMPTY)\nPRINT ROUND(EMPTY)"));
    }

    [Fact]
    public void PureEvaluatorValidatesArityBeforeVariableResolution()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate("ROUND(1,missing,missing)"));
        Assert.Contains("ROUND", error.Message);
    }

    [Fact]
    public async Task ReportsSourceLineAndSupportsExcept()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT ROUND(1,21)"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT ROUND(1,21)\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateArguments()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT ROUND(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input, Value digits) =>
        ExpressionEvaluator.Evaluate("ROUND(input,digits)",
            new Dictionary<string, Value> { ["input"] = input, ["digits"] = digits });
}
