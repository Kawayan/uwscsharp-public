using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SinConversionTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, .8414709848078965)]
    [InlineData(-1, -.8414709848078965)]
    [InlineData(1.5707963267948966, 1)]
    [InlineData(-1.5707963267948966, -1)]
    [InlineData(1e20, -.6452512852657808)]
    [InlineData(double.MaxValue, .004961954789184062)]
    [InlineData(double.Epsilon, double.Epsilon)]
    public void UsesRadiansAcrossTheFiniteDoubleRange(double input, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(input)).Number, 15);
    }

    [Fact]
    public void PreservesTinyAnglesAndHasBoundedOddResults()
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
            Assert.InRange(result, -1d, 1d);
            Assert.Equal(-result, Evaluate(Value.Numeric(-input)).Number);
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
    [InlineData("1", .8414709848078965)]
    [InlineData("-1", -.8414709848078965)]
    [InlineData("1e-300", 1e-300)]
    [InlineData("1E-300", 1e-300)]
    [InlineData("\t 1 \r\n", .8414709848078965)]
    [InlineData("1.", .8414709848078965)]
    [InlineData("1e-999", 0)]
    public void ParsesInvariantFiniteText(string text, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(text)).Number);
    }

    [Fact]
    public void AcceptsLongTextWithoutThePredicateLengthLimit()
    {
        Assert.Equal(Math.Sin(1), Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(0, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(Math.Sin(4)),
            ExpressionEvaluator.Evaluate("SIN($4)"));
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
    [InlineData("SIN()")]
    [InlineData("SIN(,)")]
    [InlineData("SIN(,Tick())")]
    [InlineData("SIN(Tick(),Tick())")]
    [InlineData("SIN(Tick(),Tick(),Tick(),Tick())")]
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
        Assert.Equal(["-0.7568024953079282", "1"], await Run(
            "PUBLIC calls=0\nPRINT sIn(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=4*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("SIN(1,missing)"));
        Assert.Contains("SIN", error.Message);
    }

    [Fact]
    public async Task ReportsSourceLineAndSupportsScriptExcept()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("PRINT 1\nPRINT SIN(\"$FFFFFFFF\")"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT SIN(\"$FFFFFFFF\")\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateTheArgument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT SIN(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("SIN(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
