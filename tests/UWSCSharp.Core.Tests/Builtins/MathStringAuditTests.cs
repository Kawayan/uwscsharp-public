using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class MathStringAuditTests
{
    public static IEnumerable<object[]> InvalidNumericInputs =>
        new[] { "ABS", "ZCUT", "ROUND", "SQRT", "POWER", "SIN", "COS", "TAN",
            "EXP", "LN", "LOGN", "CEIL", "ARCSIN", "ARCCOS", "ARCTAN" }
        .Select(name => new object[] { name + "(\"bad\""
            + (name is "POWER" or "LOGN" ? ",1)" : ")") });

    [Theory]
    [MemberData(nameof(InvalidNumericInputs))]
    public void NonNumericTextIsRejectedByEveryAuditedMathFunction(string expression)
    {
        Assert.Throws<InvalidOperationException>(() => ExpressionEvaluator.Evaluate(expression));
    }

    [Fact]
    public void FractionalPowerUsesTheDotNetSignOfZero()
    {
        Value result = ExpressionEvaluator.Evaluate("POWER(-0,0.5)");
        Assert.Equal(0, BitConverter.DoubleToInt64Bits(result.AsNumber()));
    }

    [Theory]
    [InlineData("ABS(\"-2.5\")", 2.5)]
    [InlineData("ZCUT(\"-2.5\")", 0)]
    [InlineData("ROUND(\"3.5\",\"0\")", 4)]
    [InlineData("SQRT(\"0.25\")", 0.5)]
    [InlineData("POWER(\"2\",\"-2\")", 0.25)]
    [InlineData("SIN(\"0\")", 0)]
    [InlineData("COS(\"0\")", 1)]
    [InlineData("TAN(\"0\")", 0)]
    [InlineData("EXP(\"0\")", 1)]
    [InlineData("LN(\"1\")", 0)]
    [InlineData("LOGN(\"2\",\"8\")", 3)]
    [InlineData("CEIL(\"1.1\")", 2)]
    [InlineData("ARCSIN(\"0\")", 0)]
    [InlineData("ARCCOS(\"1\")", 0)]
    [InlineData("ARCTAN(\"0\")", 0)]
    public void AdoptedMathInputsAcceptNumericStrings(string expression, double expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).AsNumber(), 10);
    }

    [Theory]
    [InlineData("POWER(0,0)", 1)]
    [InlineData("POWER(0,0.5)", 0)]
    [InlineData("POWER(1,-2147483647)", 1)]
    [InlineData("POWER(1,-2147483648)", 1)]
    public void PowerUsesConsistentFiniteDoubleRules(string expression, double expected)
    {
        Assert.Equal(Value.Numeric(expected), ExpressionEvaluator.Evaluate(expression));
    }

    [Theory]
    [InlineData("SQRT(\"-1\")")]
    [InlineData("POWER(0,-1)")]
    [InlineData("POWER(0,-2147483647)")]
    [InlineData("POWER(\"0\",\"-0.5\")")]
    [InlineData("POWER(\"0\",\"-2147483648\")")]
    [InlineData("POWER(-0,-0.5)")]
    [InlineData("POWER(\"-1\",\"0.5\")")]
    [InlineData("LN(\"0\")")]
    [InlineData("LOGN(\"1\",\"2\")")]
    [InlineData("ARCSIN(\"2\")")]
    [InlineData("ARCCOS(\"-2\")")]
    [InlineData("EXP(\"1000\")")]
    [InlineData("ROUND(\"1\",\"21\")")]
    public async Task InvalidDomainsRetainSourceLine(string expression)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("// heading\nPRINT " + expression, _ => { }));
        Assert.Equal(2, error.Line);
    }
}
