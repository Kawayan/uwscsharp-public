using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class BuiltinTests
{
    [Theory]
    [InlineData("COPY(\"A😀BC\",2,2)", "😀B")]
    [InlineData("COPY(\"abc\",2)", "bc")]
    [InlineData("COPY(\"abc\",9)", "")]
    [InlineData("BETWEENSTR(\"AxxC\",\"A\",\"C\")", "xx")]
    [InlineData("BETWEENSTR(\"AxxC\",,\"C\")", "Axx")]
    [InlineData("BETWEENSTR(\"AxxC\",\"A\")", "xxC")]
    [InlineData("BETWEENSTR(\"AxxC\")", "AxxC")]
    [InlineData("BETWEENSTR(\"AC\",\"A\",\"C\")", "")]
    [InlineData("REPLACE(\"AbAB\",\"ab\",\"X\")", "XX")]
    [InlineData("CHGMOJ(\"abc\",\"b\",\"x\")", "axc")]
    [InlineData("TRIM(\" <#TAB>a<#CR> \" )", "a")]
    [InlineData("TRIM(\"　a　\")", "　a　")]
    [InlineData("TRIM(\"　a　\",TRUE)", "a")]
    [InlineData("CHR(65)", "A")]
    [InlineData("CHR($1F600)", "😀")]
    [InlineData("FORMAT(12.5,6,1)", "  12.5")]
    [InlineData("FORMAT(255,4,-1)", "  FF")]
    [InlineData("FORMAT(\"-\",3)", "---")]
    public void PureStringFunctions(string expression,
        string expected) => Assert.Equal(Value.String(expected),
        ExpressionEvaluator.Evaluate(expression));

    [Theory]
    [InlineData("LENGTH(\"A😀B\")", 3)]
    [InlineData("LENGTHS(\"A😀B\")", 4)]
    [InlineData("POS(\"ab\",\"ABabAB\",2)", 3)]
    [InlineData("POS(\"ab\",\"ABabAB\",-1)", 5)]
    [InlineData("POS(\"B\",\"A😀B\")", 3)]
    [InlineData("POS(\"aa\",\"aaaa\",2)", 2)]
    [InlineData("POS(\"aa\",\"aaaa\",-2)", 2)]
    [InlineData("POS(\"b\",\"abc\",0)", 2)]
    [InlineData("POS(\"z\",\"abc\")", 0)]
    [InlineData("ASC(\"😀\")", 128512)]
    [InlineData("ASC(\"\")", 0)]
    [InlineData("ISUNICODE(\"日本語\")", 0)]
    [InlineData("ISUNICODE(\"😀\")", 1)]
    [InlineData("CHKNUM(\"1e3\")", 1)]
    [InlineData("CHKNUM(\"1e+3\")", 1)]
    [InlineData("CHKNUM(\" 12.5 \" )", 1)]
    [InlineData("CHKNUM(\"12.5\")", 1)]
    [InlineData("CHKNUM(\"1,000\")", 0)]
    [InlineData("CHKNUM(\"$FF\")", 0)]
    [InlineData("VAL(\"$FFFFFFFF\")", -999999)]
    [InlineData("VAL(\"bad\")", -999999)]
    [InlineData("VAL(\"bad\",7)", 7)]
    [InlineData("VAL(\"bad\",\"7\")", 7)]
    [InlineData("VAL(\"1e3\")", 1000)]
    [InlineData("INT(-1.9)", -1)]
    [InlineData("INT(\"1.9\")", 1)]
    [InlineData("ABS(-5)", 5)]
    [InlineData("ZCUT(-5)", 0)]
    [InlineData("ROUND(2.5)", 2)]
    [InlineData("ROUND(3.5)", 4)]
    [InlineData("ROUND(250,2)", 200)]
    [InlineData("ROUND(1.25,-1)", 1.2)]
    [InlineData("CEIL(-1.9)", -1)]
    [InlineData("CEIL(1.1)", 2)]
    [InlineData("CEIL(2147483647.1)", 2147483648)]
    [InlineData("SQRT(9)", 3)]
    [InlineData("POWER(2,-2)", 0.25)]
    [InlineData("SIN(0)", 0)]
    [InlineData("COS(0)", 1)]
    [InlineData("TAN(0)", 0)]
    [InlineData("EXP(0)", 1)]
    [InlineData("LN(1)", 0)]
    [InlineData("LOGN(2,8)", 3)]
    [InlineData("ARCSIN(0)", 0)]
    [InlineData("ARCCOS(1)", 0)]
    [InlineData("ARCTAN(0)", 0)]
    public void PureNumericFunctions(string expression,
        double expected) => Assert.Equal(expected,
        ExpressionEvaluator.Evaluate(expression).AsNumber(), 10);

    [Theory]
    [InlineData("SQRT(-1)")]
    [InlineData("LN(0)")]
    [InlineData("POWER(-1,0.5)")]
    [InlineData("ROUND(1,21)")]
    [InlineData("ARCSIN(2)")]
    [InlineData("EXP(1000)")]
    [InlineData("COPY(\"a\")")]
    [InlineData("COPY(,2)")]
    [InlineData("INT()")]
    [InlineData("CHR(-1)")]
    [InlineData("FORMAT(1,-1)")]
    [InlineData("SPLIT(\"a\",\" \",FALSE,FALSE,0)")]
    public void InvalidArgumentsAreDiagnosed(
        string expression) => Assert.Throws<InvalidOperationException>(
        () => ExpressionEvaluator.Evaluate(expression));

    [Fact]
    public void MissingBetweenDelimitersReturnEmpty()
    {
        Assert.Equal(ValueKind.Empty, ExpressionEvaluator.Evaluate(
            "BETWEENSTR(\"abc\",\"missing\",\"c\")").Kind);
        Assert.Equal(ValueKind.Empty, ExpressionEvaluator.Evaluate(
            "BETWEENSTR(\"abc\",\"a\",\"missing\")").Kind);
        Assert.Equal(ValueKind.Empty, ExpressionEvaluator.Evaluate(
            "BETWEENSTR(\"abc\",,,2)").Kind);
    }

    [Fact]
    public void LegacyParserKeepsLengthAndAsciiBoundaries()
    {
        Assert.False(NumericOperations.TryParse(new string(
            '1', 33), out _));
        Assert.True(NumericOperations.TryParse("0."
            + new string('1', 40), out _));
        Assert.False(NumericOperations.TryParse("0."
            + new string('1', 63), out _));
        Assert.False(NumericOperations.TryParse("１２", out _));
        Assert.False(NumericOperations.TryParse("NaN", out _));
        Assert.False(NumericOperations.TryParse("1e+999",
            out _));
    }

    [Fact]
    public void LogicalLengthCountsUnpairedSurrogateUnits()
    {
        Assert.Equal(2, StringOperations.Length("\udc00A"));
        Assert.Equal(1, StringOperations.Length("\ud800"));
        Assert.Equal("😀", StringOperations.Copy("A😀B", 2, 1));
    }

    [Fact]
    public void RandomReproducesSeededLcgAndConsumesZeroBound()
    {
        var random = new UwscRandom(0);
        Assert.Equal(1.0 / 4294967296.0, random.Next());
        Assert.Equal(0, random.Next(0));
        uint seed = unchecked((1u * 0x08088405 + 1)
            * 0x08088405 + 1);
        Assert.Equal(seed * (1.0 / 4294967296.0), random.Next());
    }

    [Fact]
    public async Task RandomStateIsInjectedAndSharedAcrossRuns()
    {
        var runner = new Interpreter(new UwscRandom(0));
        var output = new List<string>();
        await runner.RunAsync("PRINT RANDOM(1000000)",
            output.Add);
        await runner.RunAsync("PRINT RANDOM(1000000)",
            output.Add);
        Assert.Equal(new[] { "0", "31379" }, output);
    }

    [Fact]
    public async Task OptionalArgumentOmissionUsesDescriptorDefaults() => Assert.Equal(new[] { "2", "a|b", "bc", "1.2" }, await Run("PRINT ROUND(2.5,)\nPRINT JOIN(SPLIT(\"a b\"),\"|\",,0,1)\nPRINT COPY(\"abc\",2,)\nPRINT ROUND(1.25,-1)"));

    [Fact]
    public async Task TokenUpdatesRemainderAndDistinguishesEmptyFromEnd() => Assert.Equal(new[] { "a", "", "b", "end", "" }, await Run("s=\"a,,b,\"\nPRINT TOKEN(\",\",s)\nPRINT TOKEN(\",\",s)\nPRINT TOKEN(\",\",s)\nIF s=\"\" THEN PRINT \"end\"\nPRINT TOKEN(\",\",s)"));

    [Fact]
    public void TokenRespectsQuotesAndSkipsConsecutiveDelimiters()
    {
        (string Token, Value Remainder) first = StringOperations.Token(
            ",", ",,\"a,b\",,c", true, true);
        Assert.Equal("\"a,b\"", first.Token);
        Assert.Equal(Value.String("c"), first.Remainder);
        Assert.Equal(ValueKind.Empty,
            StringOperations.Token(",", "last").Remainder.Kind);
    }

    [Fact]
    public async Task PureBuiltinCallsKeepLeftToRightEffects() => Assert.Equal(new[] { "1", "2", "1" }, await Run("PUBLIC n=0\nPRINT POWER(Tick(),Tick())\nFUNCTION Tick()\nn=n+1\nPRINT n\nRESULT=n\nFEND"));
}
