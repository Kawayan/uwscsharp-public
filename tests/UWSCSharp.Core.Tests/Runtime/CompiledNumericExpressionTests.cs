using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class CompiledNumericExpressionTests
{
    [Theory]
    [InlineData("INT(1.9)", 1)]
    [InlineData("ABS(-1)", 1)]
    [InlineData("ZCUT(1)", 1)]
    [InlineData("ROUND(1.1)", 1)]
    [InlineData("SQRT(1)", 1)]
    [InlineData("POWER(1,2)", 1)]
    [InlineData("SIN(0)", 0)]
    [InlineData("COS(0)", 1)]
    [InlineData("TAN(0)", 0)]
    [InlineData("EXP(0)", 1)]
    [InlineData("LN(1)", 0)]
    [InlineData("LOGN(10,10)", 1)]
    [InlineData("CEIL(1)", 1)]
    [InlineData("ARCSIN(0)", 0)]
    [InlineData("ARCCOS(1)", 0)]
    [InlineData("ARCTAN(0)", 0)]
    public void AllowsBoundedNumericFunctions(string source, double expected)
    {
        Assert.Equal(expected, CompiledNumericExpression.Compile(source, [])
            .Evaluate(new Dictionary<string, double>()));
    }

    [Theory]
    [InlineData("FOPEN(1)")]
    [InlineData("0 AND FOPEN(1)")]
    [InlineData("1 OR EVAL(1)")]
    [InlineData("CALL(1)")]
    [InlineData("THREAD(1)")]
    [InlineData("RANDOM(1)")]
    [InlineData("LENGTH(1)")]
    [InlineData("CALCARRAY(1)")]
    [InlineData("\"1\"")]
    [InlineData("a[0]")]
    [InlineData("a.value")]
    [InlineData("EMPTY")]
    [InlineData("NULL")]
    [InlineData("NOTHING")]
    [InlineData("UNKNOWN")]
    [InlineData("ABS()")]
    [InlineData("ABS(1,2)")]
    [InlineData("ROUND(1,,)")]
    [InlineData("1e999")]
    public void RejectsEntireUnsupportedTreeBeforeEvaluation(string source)
    {
        Assert.Throws<InvalidOperationException>(() =>
            CompiledNumericExpression.Compile(source, ["a"]));
    }

    [Fact]
    public void VariablePolicyIsCopiedAndInputsAreCaseInsensitive()
    {
        string[] names = ["YY"];
        var expression = CompiledNumericExpression.Compile("yy+1", names);
        names[0] = "different";
        var values = new Dictionary<string, double> { ["yY"] = 2026 };
        Assert.Equal(2027, expression.Evaluate(values));
        values["yY"] = 2027;
        Assert.Equal(2028, expression.Evaluate(values));
        Assert.Throws<InvalidOperationException>(() =>
            expression.Evaluate(new Dictionary<string, double>()));
        values["yY"] = double.NaN;
        Assert.Throws<InvalidOperationException>(() => expression.Evaluate(values));
    }

    [Fact]
    public void EnforcesSourceTokenAndTreeLimits()
    {
        Assert.Equal(1, CompiledNumericExpression.Compile(
            new string(' ', 4095) + "1", []).Evaluate(new Dictionary<string, double>()));
        Assert.Throws<InvalidOperationException>(() =>
            CompiledNumericExpression.Compile(new string(' ', 4096) + "1", []));
        Assert.Equal(1, CompiledNumericExpression.Compile(
            new string('(', 31) + "1" + new string(')', 31), [])
            .Evaluate(new Dictionary<string, double>()));
        Assert.Throws<InvalidOperationException>(() => CompiledNumericExpression.Compile(
            new string('(', 32) + "1" + new string(')', 32), []));
        string balanced = Balanced(256);
        Assert.Equal(256, CompiledNumericExpression.Compile("+(" + balanced + ")", [])
            .Evaluate(new Dictionary<string, double>()));
        Assert.Throws<InvalidOperationException>(() =>
            CompiledNumericExpression.Compile("((" + balanced + "))", []));
    }

    [Theory]
    [InlineData("1/0")]
    [InlineData("0 AND (1/0)")]
    [InlineData("SQRT(-1)")]
    [InlineData("EXP(10000)")]
    public void KeepsDomainErrorsAndEagerOperators(string source)
    {
        var expression = CompiledNumericExpression.Compile(source, []);
        Assert.Throws<InvalidOperationException>(() =>
            expression.Evaluate(new Dictionary<string, double>()));
    }

    private static string Balanced(int leaves) => leaves == 1
        ? "1" : "(" + Balanced(leaves / 2) + "+" + Balanced(leaves / 2) + ")";
}
