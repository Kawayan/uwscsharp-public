using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ValContractTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("+.5", .5)]
    [InlineData("1.", 1)]
    [InlineData(" 1.9 ", 1.9)]
    [InlineData("\t1\r\n", 1)]
    [InlineData("1e3", 1000)]
    [InlineData("-1E-3", -.001)]
    [InlineData("1e-999", 0)]
    [InlineData("1.7976931348623157e+308", double.MaxValue)]
    public void FiniteTextMatchesRuntimeNumberAndChkNum(string text, double expected)
    {
        Assert.Equal(Value.Numeric(expected), Evaluate(Value.String(text)));
        Assert.Equal(expected, NumericOperations.RuntimeNumber(Value.String(text)));
        Assert.Equal(Value.Numeric(1), ExpressionEvaluator.Evaluate("CHKNUM(input)",
            new Dictionary<string, Value> { ["input"] = Value.String(text) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.Epsilon)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void FiniteNumbersKeepTheirValue(double value)
    {
        Assert.Equal(Value.Numeric(value), Evaluate(Value.Numeric(value)));
    }

    [Theory]
    [InlineData("-0")]
    [InlineData("-1e-999")]
    public void TextKeepsNegativeZero(string text)
    {
        Assert.Equal(long.MinValue,
            BitConverter.DoubleToInt64Bits(Evaluate(Value.String(text)).Number));
    }

    [Fact]
    public void NumericNegativeZeroIsNotReparsed()
    {
        double zero = BitConverter.Int64BitsToDouble(long.MinValue);
        Assert.Equal(long.MinValue,
            BitConverter.DoubleToInt64Bits(Evaluate(Value.Numeric(zero)).Number));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad")]
    [InlineData("$FF")]
    [InlineData("0x10")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1d2")]
    [InlineData("１.９")]
    [InlineData("1,234.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("1\0")]
    [InlineData("1\0x")]
    [InlineData("1\u0145+2")]
    public void InvalidTextReturnsDefaultOrConvertedFallback(string text)
    {
        Assert.Equal(Value.Numeric(-999999), Evaluate(Value.String(text)));
        Assert.Equal(Value.Numeric(7), Evaluate(Value.String(text), Value.String("7")));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteAdapterInputUsesFallback(double value)
    {
        Assert.Equal(Value.Numeric(7), Evaluate(new Value(ValueKind.Number, value), Value.Numeric(7)));
    }

    [Fact]
    public void EmptyInputAndOmittedFallbackHaveDistinctDefaults()
    {
        Assert.Equal(Value.Numeric(-999999), Evaluate(Value.Empty));
        Assert.Equal(Value.Numeric(0), Evaluate(Value.String("bad"), Value.Empty));
        Assert.Equal(Value.Numeric(-999999), Evaluate(Value.String("bad"), new Value(ValueKind.Omitted)));
    }

    [Fact]
    public void LongFiniteInputAndFallbackHaveNoLegacyLengthLimit()
    {
        var text = Value.String(new string('0', 10000) + "1");
        Assert.Equal(Value.Numeric(1), Evaluate(text));
        Assert.Equal(Value.Numeric(1), Evaluate(Value.String("bad"), text));
        Assert.Equal(Value.Numeric(0), Evaluate(Value.String("0." + new string('0', 10000))));
        Assert.Equal(Value.Numeric(-999999), Evaluate(Value.String(new string('9', 10000))));
    }

    [Fact]
    public void UnsupportedPrimaryKindsAreTypeErrorsEvenWithValidFallback()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted),
            Value.Nothing, Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Evaluate(value, Value.Numeric(7)));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void UnusedFallbackIsNotConvertedButUsedFallbackMustBeFiniteNumeric()
    {
        Value[] invalid = [new(ValueKind.Null), Value.Nothing,
            Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(UwscArray.FromValues([Value.Numeric(1)])),
            Value.FromHashTable(UwscHashTable.Create()),
            Value.String("bad"), Value.String("$4"),
            Value.String("1e309"), Value.String("1\0"),
            new(ValueKind.Number, double.NaN),
            new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value fallback in invalid)
        {
            Assert.Equal(Value.Numeric(1), Evaluate(Value.String("1"), fallback));
            Assert.Throws<InvalidOperationException>(() => Evaluate(Value.String("bad"), fallback));
        }
    }

    [Theory]
    [InlineData("VAL()")]
    [InlineData("VAL(,)")]
    [InlineData("VAL(,Tick())")]
    [InlineData("VAL(Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentSideEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + call + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ArgumentsRunLeftToRightOnceEvenWhenFallbackConversionIsUnused()
    {
        Assert.Equal(["input", "fallback", "2", "2"], await Run("""
            PUBLIC calls=0
            PRINT vAl(InputValue(),Fallback())
            PRINT calls
            FUNCTION InputValue()
                calls=calls+1
                PRINT "input"
                RESULT="2"
            FEND
            FUNCTION Fallback()
                calls=calls+1
                PRINT "fallback"
                RESULT="bad"
            FEND
            """));
        Assert.Equal(["caught"], await Run(
            "TRY\nPRINT VAL(\"1\",1/0)\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task OmittedFallbackAndHexLiteralAreAccepted()
    {
        Assert.Equal(["-999999", "255", "1", "0"], await Run(
            "PRINT VAL(\"bad\",)\nPRINT VAL($FF)\nPRINT VAL(TRUE)\nPRINT VAL(FALSE)"));
    }

    [Fact]
    public void PureEvaluatorChecksArityBeforeResolvingVariables()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ExpressionEvaluator.Evaluate("VAL(1,2,missing)"));
        Assert.Contains("VAL", error.Message);
    }

    [Fact]
    public async Task InvalidFallbackHasSourceLineAndIsCatchable()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT VAL(\"bad\",\"bad\")"));
        Assert.Equal(2, error.Line);
        Assert.Equal(["caught"], await Run("TRY\nPRINT VAL(NULL,7)\n"
            + "EXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT VAL(Tick(),Tick())\n"
                + "FUNCTION Tick()\nPRINT \"side effect\"\nRESULT=1\nFEND",
                output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Evaluate(Value input) => ExpressionEvaluator.Evaluate(
        "VAL(input)", new Dictionary<string, Value> { ["input"] = input });

    private static Value Evaluate(Value input, Value fallback) => ExpressionEvaluator.Evaluate(
        "VAL(input,fallback)", new Dictionary<string, Value>
        {
            ["input"] = input,
            ["fallback"] = fallback
        });
}
