using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class CosConversionTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, .5403023058681398)]
    [InlineData(-1, .5403023058681398)]
    [InlineData(3.141592653589793, -1)]
    [InlineData(-3.141592653589793, -1)]
    [InlineData(1e20, .7639704044417283)]
    [InlineData(double.MaxValue, -.9999876894265599)]
    [InlineData(double.Epsilon, 1)]
    public void UsesRadiansAcrossTheFiniteDoubleRange(double input, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(input)).Number, 15);
    }

    [Fact]
    public void TinyAnglesRoundToOneAndFiniteResultsAreBoundedAndEven()
    {
        for (int exponent = -1074; exponent <= -27; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            Assert.Equal(1, Evaluate(Value.Numeric(input)).Number);
            Assert.Equal(1, Evaluate(Value.Numeric(-input)).Number);
        }

        for (int exponent = -26; exponent <= 1023; exponent++)
        {
            double input = Math.ScaleB(1, exponent);
            double result = Evaluate(Value.Numeric(input)).Number;
            Assert.InRange(result, -1d, 1d);
            Assert.Equal(result, Evaluate(Value.Numeric(-input)).Number);
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
    public void ChangesSignAcrossTheQuarterTurnBoundary()
    {
        double quarterTurn = Math.PI / 2;
        double before = Math.BitDecrement(quarterTurn);
        double after = Math.BitIncrement(quarterTurn);
        Assert.True(Evaluate(Value.Numeric(before)).Number > 0);
        Assert.True(Evaluate(Value.Numeric(after)).Number < 0);
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
    [InlineData("1", .5403023058681398)]
    [InlineData("-1", .5403023058681398)]
    [InlineData("1e-300", 1)]
    [InlineData("1E-300", 1)]
    [InlineData("\t 1 \r\n", .5403023058681398)]
    [InlineData("1.", .5403023058681398)]
    [InlineData("1e-999", 1)]
    public void ParsesInvariantFiniteText(string text, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(text)).Number);
    }

    [Fact]
    public void AcceptsLongTextWithoutThePredicateLengthLimit()
    {
        Assert.Equal(Math.Cos(1), Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(1, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(Math.Cos(4)),
            ExpressionEvaluator.Evaluate("COS($4)"));
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
    [InlineData("COS()")]
    [InlineData("COS(,)")]
    [InlineData("COS(,Tick())")]
    [InlineData("COS(Tick(),Tick())")]
    [InlineData("COS(Tick(),Tick(),Tick(),Tick())")]
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
        Assert.Equal(["-0.6536436208636119", "1"], await Run(
            "PUBLIC calls=0\nPRINT cOs(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=4*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("COS(1,missing)"));
        Assert.Contains("COS", error.Message);
    }

    [Fact]
    public async Task ReportsSourceLineAndSupportsScriptExcept()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("PRINT 1\nPRINT COS(\"$FFFFFFFF\")"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT COS(\"$FFFFFFFF\")\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateTheArgument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT COS(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("COS(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
