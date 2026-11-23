using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SqrtConversionTests
{
    [Fact]
    public void ExactPowerOfTwoSquaresHaveExactRootsAcrossTheFiniteRange()
    {
        for (int exponent = -537; exponent <= 511; exponent++)
        {
            double square = Math.ScaleB(1, 2 * exponent);
            double expected = Math.ScaleB(1, exponent);
            Value result = Evaluate(Value.Numeric(square));
            Assert.Equal(ValueKind.Number, result.Kind);
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected),
                BitConverter.DoubleToInt64Bits(result.Number));
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(.25, .5)]
    [InlineData(2, 1.4142135623730951)]
    [InlineData(double.MaxValue, 1.3407807929942596e154)]
    public void UsesDoubleSquareRoots(double input, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(input)).Number);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-.25)]
    [InlineData(double.MinValue)]
    [InlineData(-double.Epsilon)]
    public void NegativeValuesHaveAnExplicitDomainError(double input)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Evaluate(Value.Numeric(input)));
        Assert.Contains("0以上", error.Message);
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
    [InlineData("2.25", 1.5)]
    [InlineData("1e2", 10)]
    [InlineData("1E+2", 10)]
    [InlineData("\t .25 \r\n", .5)]
    [InlineData("1.", 1)]
    [InlineData("1e-999", 0)]
    [InlineData(".25", .5)]
    public void ParsesInvariantFiniteText(string text, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(text)).Number);
    }

    [Fact]
    public void AcceptsLongTextWithoutThePredicateLengthLimit()
    {
        Assert.Equal(1, Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(0, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(2),
            ExpressionEvaluator.Evaluate("SQRT($4)"));
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
    [InlineData("SQRT()")]
    [InlineData("SQRT(,)")]
    [InlineData("SQRT(,Tick())")]
    [InlineData("SQRT(Tick(),Tick())")]
    [InlineData("SQRT(Tick(),Tick(),Tick(),Tick())")]
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
        Assert.Equal(["2", "1"], await Run(
            "PUBLIC calls=0\nPRINT sQrT(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=4*calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("SQRT(1,missing)"));
        Assert.Contains("SQRT", error.Message);
    }

    [Fact]
    public async Task ReportsSourceLineAndSupportsScriptExcept()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("PRINT 1\nPRINT SQRT(\"$FFFFFFFF\")"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT SQRT(\"$FFFFFFFF\")\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateTheArgument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT SQRT(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("SQRT(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
