using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ArcTanConversionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, .7853981633974483)]
    [InlineData(-1, -.7853981633974483)]
    [InlineData(.5, .4636476090008061)]
    [InlineData(-.5, -.4636476090008061)]
    [InlineData(2, 1.1071487177940904)]
    [InlineData(-2, -1.1071487177940904)]
    [InlineData(double.MaxValue, 1.5707963267948966)]
    [InlineData(double.MinValue, -1.5707963267948966)]
    [InlineData(double.Epsilon, double.Epsilon)]
    public void ReturnsRadianAnglesAcrossTheFiniteDoubleRange(double input, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(input)).Number, 15);
    }

    [Fact]
    public void TinyInputsArePreservedAndFinitePowersAreBoundedNondecreasingAndOdd()
    {
        for (int exponent = -1074; exponent <= -27; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            Assert.Equal(input, Evaluate(Value.Numeric(input)).Number);
            Assert.Equal(-input, Evaluate(Value.Numeric(-input)).Number);
        }

        double previous = 0;
        for (int exponent = -26; exponent <= 1023; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            double result = Evaluate(Value.Numeric(input)).Number;
            Assert.InRange(result, 0, Math.PI / 2);
            Assert.True(result >= previous);
            Assert.Equal(-result, Evaluate(Value.Numeric(-input)).Number);
            previous = result;
        }
    }

    [Fact]
    public void ReciprocalPositivePowersHaveComplementaryAngles()
    {
        for (int exponent = -1023; exponent <= 1023; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            double first = Evaluate(Value.Numeric(input)).Number;
            double second = Evaluate(Value.Numeric(1 / input)).Number;
            Assert.InRange(Math.Abs(first + second - Math.PI / 2), 0, 4e-16);
        }
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
    [InlineData("1", .7853981633974483)]
    [InlineData("-1", -.7853981633974483)]
    [InlineData(".5", .4636476090008061)]
    [InlineData("1e-300", 1e-300)]
    [InlineData("1E-300", 1e-300)]
    [InlineData("\t .5 \r\n", .4636476090008061)]
    [InlineData("1.", .7853981633974483)]
    [InlineData("1e-999", 0)]
    [InlineData("1e308", 1.5707963267948966)]
    [InlineData("-1.7976931348623157e+308", -1.5707963267948966)]
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
        Assert.Equal(Math.Atan(1), Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(0, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(Math.Atan(4)),
            ExpressionEvaluator.Evaluate("ARCTAN($4)"));
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
    [InlineData("ARCTAN()")]
    [InlineData("ARCTAN(,)")]
    [InlineData("ARCTAN(,Tick())")]
    [InlineData("ARCTAN(Tick(),Tick())")]
    [InlineData("ARCTAN(Tick(),Tick(),Tick(),Tick())")]
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
        Assert.Equal(["0.4636476090008061", "1"], await Run(
            "PUBLIC calls=0\nPRINT aRcTaN(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=.5*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("ARCTAN(1,missing)"));
        Assert.Contains("ARCTAN", error.Message);
    }

    [Fact]
    public async Task ReportsSourceLineAndSupportsScriptExcept()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("PRINT 1\nPRINT ARCTAN(\"$FFFFFFFF\")"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT ARCTAN(\"$FFFFFFFF\")\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateTheArgument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT ARCTAN(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("ARCTAN(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
