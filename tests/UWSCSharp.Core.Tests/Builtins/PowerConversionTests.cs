using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class PowerConversionTests
{
    [Theory]
    [InlineData(2, 3, 8)]
    [InlineData(2, -2, .25)]
    [InlineData(4, .5, 2)]
    [InlineData(.25, -.5, 2)]
    [InlineData(-2, 3, -8)]
    [InlineData(-2, 4, 16)]
    [InlineData(-2, -3, -.125)]
    [InlineData(0, 0, 1)]
    [InlineData(0, .5, 0)]
    [InlineData(0, 2147483648d, 0)]
    [InlineData(1, -2147483648d, 1)]
    [InlineData(-1, 2147483647d, -1)]
    [InlineData(-1, 2147483648d, 1)]
    [InlineData(-1, 9007199254740991d, -1)]
    [InlineData(-1, 9007199254740992d, 1)]
    public void UsesTheFiniteDotNetPowerContract(double basis, double exponent, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.Numeric(basis), Value.Numeric(exponent)).Number);
    }

    [Fact]
    public void PowerOfTwoHasExactResultsAcrossFiniteBinaryExponents()
    {
        for (int exponent = -1074; exponent <= 1023; exponent++)
        {
            double expected = Math.ScaleB(1, exponent);
            Value result = Evaluate(Value.Numeric(2), Value.Numeric(exponent));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected),
                BitConverter.DoubleToInt64Bits(result.Number));
        }
    }

    [Theory]
    [InlineData(3, long.MinValue)]
    [InlineData(2, 0L)]
    [InlineData(.5, 0L)]
    public void SignOfNegativeZeroFollowsDotNet(double exponent, long expected)
    {
        var input = Value.Numeric(BitConverter.Int64BitsToDouble(long.MinValue));
        Assert.Equal(expected, BitConverter.DoubleToInt64Bits(
            Evaluate(input, Value.Numeric(exponent)).Number));
    }

    [Theory]
    [InlineData(0, -.5)]
    [InlineData(0, -1)]
    [InlineData(0, -2147483648d)]
    [InlineData(-2, .5)]
    [InlineData(-2, -.5)]
    [InlineData(1e308, 2)]
    public void RejectsInvalidRealDomainsAndOverflow(double basis, double exponent)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(Value.Numeric(basis), Value.Numeric(exponent)));
    }

    [Fact]
    public void AllowsUnderflowAndEmptyValues()
    {
        Assert.Equal(0, Evaluate(Value.Numeric(1e-300), Value.Numeric(2)).Number);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(
            Evaluate(Value.Numeric(-1e-300), Value.Numeric(3)).Number));
        Assert.Equal(0, Evaluate(default, Value.Numeric(2)).Number);
        Assert.Equal(1, Evaluate(Value.Numeric(2), default).Number);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonfiniteInputsEvenForOtherwiseConstantResults(double input)
    {
        Value invalid = new(ValueKind.Number, input);
        Assert.Throws<InvalidOperationException>(() => Evaluate(invalid, default));
        Assert.Throws<InvalidOperationException>(() => Evaluate(Value.Numeric(1), invalid));
    }

    [Fact]
    public void RejectsUnsupportedKindsAtBothArgumentsWithoutMutation()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted), Value.Nothing,
            Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Evaluate(value, default));
            Assert.Throws<InvalidOperationException>(() => Evaluate(default, value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("2", "-2", .25)]
    [InlineData("1e2", ".5", 10)]
    [InlineData("\t .25 \r\n", "-.5", 2)]
    [InlineData("000000000000000000000000000000001", "1E2", 1)]
    public void ConvertsBothArgumentsUsingTheInvariantGrammar(
        string basis, string exponent, double expected)
    {
        Assert.Equal(expected, Evaluate(Value.String(basis), Value.String(exponent)).Number);
    }

    [Theory]
    [InlineData("$4")]
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
    [InlineData("POWER()")]
    [InlineData("POWER(Tick())")]
    [InlineData("POWER(,Tick())")]
    [InlineData("POWER(Tick(),)")]
    [InlineData("POWER(Tick(),Tick(),Tick())")]
    [InlineData("POWER(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityDoesNotEvaluateArguments(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=calls\nFEND"));
    }

    [Fact]
    public async Task EvaluatesBothArgumentsOnceBeforeConversion()
    {
        Assert.Equal(["1", "2", "1", "2"], await Run(
            "PUBLIC calls=0\nPRINT pOwEr(Tick(),Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nPRINT calls\nRESULT=calls\nFEND"));
        Assert.Equal(["caught", "1"], await Run(
            "PUBLIC calls=0\nTRY\nPRINT POWER(\"bad\",Tick())\n"
            + "EXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeVariableResolution()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate("POWER(missing)"));
        Assert.Contains("POWER", error.Message);
    }

    [Fact]
    public async Task DomainErrorsKeepSourceLineAndSupportExcept()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT POWER(0,-.5)"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT POWER(0,-.5)\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationDoesNotEvaluateArguments()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT POWER(Tick(),Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value basis, Value exponent) =>
        ExpressionEvaluator.Evaluate("POWER(basis,exponent)",
            new Dictionary<string, Value> { ["basis"] = basis, ["exponent"] = exponent });
}
