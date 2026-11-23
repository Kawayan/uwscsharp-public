using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class FormatContractTests
{
    [Theory]
    [InlineData(2.5, 0, "2")]
    [InlineData(3.5, 0, "4")]
    [InlineData(-2.5, 0, "-2")]
    [InlineData(0.125, 2, "0.12")]
    [InlineData(1.005, 2, "1.00")]
    [InlineData(1.25, 1, "1.2")]
    [InlineData(double.Epsilon, 2, "0.00")]
    public void FixedPointUsesInvariantDotNetRounding(double value, int digits, string expected)
    {
        Assert.Equal(expected, Format(Value.Numeric(value), Value.Numeric(0), Value.Numeric(digits)));
    }

    [Theory]
    [InlineData(255, "FF")]
    [InlineData(2147483648, "80000000")]
    [InlineData(4294967295, "FFFFFFFF")]
    [InlineData(9223372036854774784d, "7FFFFFFFFFFFFC00")]
    [InlineData(-9223372036854775808d, "8000000000000000")]
    [InlineData(-1, "FFFFFFFFFFFFFFFF")]
    [InlineData(-1.9, "FFFFFFFFFFFFFFFF")]
    [InlineData(1.9, "1")]
    public void HexUsesTruncatedSignedInt64(double input, string expected)
    {
        Assert.Equal(expected, Format(Value.Numeric(input), Value.Numeric(0), Value.Numeric(-1)));
    }

    [Theory]
    [InlineData(9223372036854775808d)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void HexRejectsValuesOutsideSignedInt64(double value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Format(Value.Numeric(value), Value.Numeric(0), Value.Numeric(-1)));
    }

    [Fact]
    public void FixedPointHandlesFullFiniteRangeAndKeepsNegativeZero()
    {
        string positive = Format(Value.Numeric(double.MaxValue), Value.Numeric(0), Value.Numeric(20));
        Assert.Equal(330, positive.Length);
        Assert.StartsWith("1797693134", positive);
        Assert.EndsWith(".00000000000000000000", positive);
        Assert.Equal("-" + positive,
            Format(Value.Numeric(double.MinValue), Value.Numeric(0), Value.Numeric(20)));
        double zero = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Equal("-0.00", Format(Value.Numeric(zero), Value.Numeric(0), Value.Numeric(2)));
        Assert.Throws<InvalidOperationException>(() =>
            Format(Value.Numeric(Math.BitDecrement(-9223372036854775808d)),
                Value.Numeric(0), Value.Numeric(-1)));
    }

    [Theory]
    [InlineData(" 6 ", "1", "  12.5")]
    [InlineData("1e1", "2", "     12.50")]
    [InlineData("6.9", "1.9", "  12.5")]
    public void WidthAndPrecisionUseFiniteTextThenTruncate(
        string width, string digits, string expected)
    {
        Assert.Equal(expected, Format(Value.Numeric(12.5), Value.String(width), Value.String(digits)));
    }

    [Fact]
    public void LongWidthTextAndOptionalPrecisionUseTheirDefaults()
    {
        Assert.Equal("  1", Format(Value.Numeric(1),
            Value.String(new string('0', 10000) + "3"), new Value(ValueKind.Omitted)));
        Assert.Equal("1", Format(Value.Numeric(1), Value.Numeric(-.5), Value.Empty));
        Assert.Equal("  FF", Format(Value.Numeric(255), Value.Numeric(4), Value.Numeric(-1.9)));
    }

    [Theory]
    [InlineData("ab", 5, 0, "ababa")]
    [InlineData("123", 4, -1, "1231")]
    [InlineData("NaN", 3, 0, "NaN")]
    [InlineData("\0", 3, 0, "\0\0\0")]
    [InlineData("", 1000000, 20, "")]
    [InlineData("abc", 0, 0, "")]
    public void StringInputAlwaysUsesTheFillLane(
        string unit, int width, int digits, string expected)
    {
        Assert.Equal(expected, Format(Value.String(unit), Value.Numeric(width), Value.Numeric(digits)));
    }

    [Fact]
    public void NumericWidthIsMinimumAndStringWidthIsExact()
    {
        Assert.Equal("1.25", Format(Value.Numeric(1.25), Value.Numeric(2), Value.Numeric(2)));
        Assert.Equal("FF", Format(Value.Numeric(255), Value.Numeric(1), Value.Numeric(-1)));
        Assert.Equal("a", Format(Value.String("ab"), Value.Numeric(1), Value.Numeric(0)));
        Assert.Equal("  0", Format(Value.Empty, Value.Numeric(3), Value.Empty));
    }

    [Fact]
    public void OutputWidthIsBoundedIndependentlyOfArrayLimits()
    {
        string text = Format(Value.String("ab"), Value.Numeric(1000000), Value.Empty);
        Assert.Equal(1000000, text.Length);
        Assert.StartsWith("abab", text);
        Assert.EndsWith("abab", text);
        Assert.Throws<InvalidOperationException>(() =>
            Format(Value.String("ab"), Value.Numeric(1000001), Value.Empty));
    }

    [Theory]
    [InlineData("FORMAT(1,\"$4\")")]
    [InlineData("FORMAT(1,\"1e+\")")]
    [InlineData("FORMAT(1,\"1,0\")")]
    [InlineData("FORMAT(1,\"1\"+CHR(0))")]
    [InlineData("FORMAT(1,0,\"$1\")")]
    [InlineData("FORMAT(1,-1)")]
    [InlineData("FORMAT(1,0,21)")]
    [InlineData("FORMAT(1,0,-2)")]
    [InlineData("FORMAT(\"ab\",5,21)")]
    [InlineData("FORMAT(\"ab\",5,\"bad\")")]
    [InlineData("FORMAT(1,1e+308)")]
    public async Task InvalidControlArgumentsAreCatchable(string expression)
    {
        Assert.Equal(["caught"], await Run("TRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public void UnsupportedKindsAndNonfiniteValuesDoNotMutateInput()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() =>
                Format(value, Value.Numeric(0), Value.Numeric(0)));
            Assert.Throws<InvalidOperationException>(() =>
                Format(Value.Numeric(1), value, Value.Numeric(0)));
            if (value.Kind != ValueKind.Omitted)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    Format(Value.Numeric(1), Value.Numeric(0), value));
            }
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("FORMAT()")]
    [InlineData("FORMAT(Tick())")]
    [InlineData("FORMAT(,Tick())")]
    [InlineData("FORMAT(Tick(),)")]
    [InlineData("FORMAT(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentSideEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + call + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunLeftToRightOnceAndNamesIgnoreCase()
    {
        Assert.Equal(["1", "2", "3", "1.000", "3"], await Run(
            "PUBLIC calls=0\nPRINT fOrMaT(Tick(),Tick(),Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nPRINT calls\nRESULT=calls\nFEND"));
        Assert.Equal(["  1"], await Run("PRINT FORMAT(1,3,)"));
    }

    [Fact]
    public async Task InvalidHexInputReportsItsSourceLine()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT FORMAT(9223372036854775808,0,-1)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluationAndAllocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT FORMAT(\"ab\",Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=1000000\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static string Format(Value value, Value width, Value digits) =>
        BuiltinFunctions.Evaluate("FORMAT", [value, width, digits]).Text;
}
