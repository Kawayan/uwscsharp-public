using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ChkNumContractTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("1.9")]
    [InlineData("+.5")]
    [InlineData("1.")]
    [InlineData("1e3")]
    [InlineData("1E+3")]
    [InlineData("-1E-3")]
    [InlineData(" 12.5 ")]
    [InlineData("\t1\r\n")]
    [InlineData("1.7976931348623157e+308")]
    [InlineData("1e-999")]
    [InlineData("-1e-999")]
    public void FiniteTextUsesExactlyTheRuntimeConversionGrammar(string text)
    {
        Assert.Equal(Value.Numeric(1), Evaluate(Value.String(text)));
        Assert.True(double.IsFinite(NumericOperations.RuntimeNumber(Value.String(text))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(double.Epsilon)]
    public void FiniteNumberDisplayIsAccepted(double value)
    {
        Assert.Equal(Value.Numeric(1), Evaluate(Value.Numeric(value)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("$FF")]
    [InlineData("0x10")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1e-")]
    [InlineData("1d2")]
    [InlineData("１.９")]
    [InlineData("1,234.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1\0")]
    [InlineData("1\0x")]
    [InlineData("1\u0145+2")]
    public void InvalidTextReturnsFalseWithoutThrowing(string text)
    {
        Assert.Equal(Value.Numeric(0), Evaluate(Value.String(text)));
        Assert.Throws<InvalidOperationException>(() =>
            NumericOperations.RuntimeNumber(Value.String(text)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteAdapterNumbersReturnFalse(double value)
    {
        Assert.Equal(Value.Numeric(0), Evaluate(new Value(ValueKind.Number, value)));
    }

    [Fact]
    public void LongTextHasNoLegacyLengthLimitAndEmptyIsNotNumericText()
    {
        Assert.Equal(Value.Numeric(1), Evaluate(Value.String(new string('0', 10000) + "1")));
        Assert.Equal(Value.Numeric(1), Evaluate(Value.String("0." + new string('1', 10000))));
        Assert.Equal(Value.Numeric(0), Evaluate(Value.String(new string('9', 10000))));
        Assert.Equal(Value.Numeric(0), Evaluate(Value.Empty));
        Assert.Equal(Value.Numeric(1), ExpressionEvaluator.Evaluate("CHKNUM($FF)"));
    }

    [Fact]
    public void UnsupportedKindsAreTypeErrorsWithoutMutatingTheirContents()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Evaluate(value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("CHKNUM()")]
    [InlineData("CHKNUM(,)")]
    [InlineData("CHKNUM(,Tick())")]
    [InlineData("CHKNUM(Tick(),Tick())")]
    [InlineData("CHKNUM(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentSideEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + call + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndNamesAreCaseInsensitive()
    {
        Assert.Equal(["1", "1"], await Run("PUBLIC calls=0\nPRINT cHkNuM(Tick())\n"
            + "PRINT calls\nFUNCTION Tick()\ncalls=calls+1\nRESULT=\"1e3\"\nFEND"));
    }

    [Fact]
    public void PureEvaluatorRejectsExtraArgumentsBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate("CHKNUM(1,missing)"));
        Assert.Contains("CHKNUM", error.Message);
    }

    [Fact]
    public async Task TypeErrorsHaveSourceLinesAndCanBeCaught()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT CHKNUM(NULL)"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run("TRY\nPRINT CHKNUM(NULL)\n"
            + "EXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT CHKNUM(Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    [Fact]
    public async Task PublicLegacyParserRemainsSeparateFromCommonTextConversion()
    {
        Assert.False(NumericOperations.TryParse("1e3", out _));
        Assert.True(NumericOperations.TryParse("$FF", out _));
        Assert.Equal(["1", "0", "-999999", "1000"], await Run(
            "PRINT CHKNUM(\"1e3\")\nPRINT CHKNUM(\"$FF\")\n"
            + "PRINT VAL(\"$FFFFFFFF\")\nPRINT VAL(\"1e3\")"));
    }

    private static Value Evaluate(Value value) => ExpressionEvaluator.Evaluate(
        "CHKNUM(input)", new Dictionary<string, Value> { ["input"] = value });
}
