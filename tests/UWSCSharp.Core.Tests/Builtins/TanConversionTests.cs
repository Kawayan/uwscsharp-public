using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class TanConversionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1.5574077246549023)]
    [InlineData(-1, -1.5574077246549023)]
    [InlineData(.7853981633974483, .9999999999999999)]
    [InlineData(-.7853981633974483, -.9999999999999999)]
    [InlineData(1e20, -.8446024630198843)]
    [InlineData(double.MaxValue, -.004962015874444895)]
    [InlineData(double.Epsilon, double.Epsilon)]
    public void UsesRadiansAcrossTheFiniteDoubleRange(double input, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(input)).Number, 15);
    }

    [Fact]
    public void PreservesTinyAnglesAndHasFiniteOddResults()
    {
        for (int exponent = -1074; exponent <= -27; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            Assert.Equal(input, Evaluate(Value.Numeric(input)).Number);
            Assert.Equal(-input, Evaluate(Value.Numeric(-input)).Number);
        }

        for (int exponent = -26; exponent <= 1023; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            double result = Evaluate(Value.Numeric(input)).Number;
            Assert.True(double.IsFinite(result));
            Assert.Equal(-result, Evaluate(Value.Numeric(-input)).Number);
        }
    }

    [Fact]
    public void RepresentsTheQuarterTurnApproximationWithoutAnArtificialCutoff()
    {
        double quarterTurn = Math.PI / 2;
        double before = Math.BitDecrement(quarterTurn);
        double after = Math.BitIncrement(quarterTurn);
        double beforeResult = Evaluate(Value.Numeric(before)).Number;
        double atResult = Evaluate(Value.Numeric(quarterTurn)).Number;
        double afterResult = Evaluate(Value.Numeric(after)).Number;
        Assert.True(beforeResult > 1e15);
        Assert.True(atResult > beforeResult);
        Assert.True(afterResult < -1e15);
        Assert.True(double.IsFinite(atResult));
        Assert.Equal(-atResult,
            Evaluate(Value.Numeric(-quarterTurn)).Number);
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
    [InlineData("1", 1.5574077246549023)]
    [InlineData("-1", -1.5574077246549023)]
    [InlineData("1e-300", 1e-300)]
    [InlineData("1E-300", 1e-300)]
    [InlineData("\t 1 \r\n", 1.5574077246549023)]
    [InlineData("1.", 1.5574077246549023)]
    [InlineData("1e-999", 0)]
    public void ParsesInvariantFiniteText(string text, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(text)).Number);
    }

    [Fact]
    public void AcceptsLongTextWithoutThePredicateLengthLimit()
    {
        Assert.Equal(Math.Tan(1), Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(0, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(Math.Tan(4)),
            ExpressionEvaluator.Evaluate("TAN($4)"));
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
    [InlineData("TAN()")]
    [InlineData("TAN(,)")]
    [InlineData("TAN(,Tick())")]
    [InlineData("TAN(Tick(),Tick())")]
    [InlineData("TAN(Tick(),Tick(),Tick(),Tick())")]
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
        Assert.Equal(["1.1578212823495777", "1"], await Run(
            "PUBLIC calls=0\nPRINT tAn(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=4*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("TAN(1,missing)"));
        Assert.Contains("TAN", error.Message);
    }

    [Fact]
    public async Task ReportsSourceLineAndSupportsScriptExcept()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("PRINT 1\nPRINT TAN(\"$FFFFFFFF\")"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT TAN(\"$FFFFFFFF\")\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateTheArgument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT TAN(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("TAN(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
