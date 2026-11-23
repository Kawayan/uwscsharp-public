using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ZCutConversionTests
{
    [Fact]
    public void ClampsNegativeValuesAndPreservesPositiveBitsAcrossFiniteExponentClasses()
    {
        long[] fractions = [0, 1, 1L << 51, (1L << 52) - 1];
        for (long exponent = 0; exponent < 2047; exponent++)
        {
            foreach (long fraction in fractions)
            {
                long positiveBits = (exponent << 52) | fraction;
                foreach (long sign in new[] { 0L, long.MinValue })
                {
                    double input = BitConverter.Int64BitsToDouble(
                        positiveBits | sign);
                    Value result = Evaluate(Value.Numeric(input));
                    Assert.Equal(ValueKind.Number, result.Kind);
                    Assert.Equal(sign == 0 ? positiveBits : 0,
                        BitConverter.DoubleToInt64Bits(result.Number));
                }
            }
        }
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
    [InlineData("-2.5", 0)]
    [InlineData("1e2", 100)]
    [InlineData("-1E+2", 0)]
    [InlineData("\t -.5 \r\n", 0)]
    [InlineData("-1.", 0)]
    [InlineData("1e-999", 0)]
    [InlineData("0.99999999999999994448", .9999999999999999)]
    public void ParsesInvariantFiniteText(string text, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(text)).Number);
    }

    [Fact]
    public void AcceptsLongTextWithoutThePredicateLengthLimit()
    {
        Assert.Equal(1, Evaluate(Value.String(new string('0', 100) + "1")).Number);
        Assert.Equal(0, Evaluate(default).Number);
        Assert.Equal(Value.Numeric(4294967295),
            ExpressionEvaluator.Evaluate("ZCUT($FFFFFFFF)"));
    }

    [Theory]
    [InlineData("$FFFFFFFF")]
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
    [InlineData("ZCUT()")]
    [InlineData("ZCUT(,)")]
    [InlineData("ZCUT(,Tick())")]
    [InlineData("ZCUT(Tick(),Tick())")]
    [InlineData("ZCUT(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentSideEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=-calls\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndNamesAreCaseInsensitive()
    {
        Assert.Equal(["0", "1"], await Run(
            "PUBLIC calls=0\nPRINT zCuT(Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=-calls\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ExpressionEvaluator.Evaluate("ZCUT(1,missing)"));
        Assert.Contains("ZCUT", error.Message);
    }

    [Fact]
    public async Task ReportsSourceLineAndSupportsScriptExcept()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(
            () => Run("PRINT 1\nPRINT ZCUT(\"$FFFFFFFF\")"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT ZCUT(\"$FFFFFFFF\")\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateTheArgument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT ZCUT(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=-1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) =>
        ExpressionEvaluator.Evaluate("ZCUT(input)",
            new Dictionary<string, Value> { ["input"] = input });
}
