using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SourceSyntaxAuditTests
{
    [Theory]
    [InlineData("\"a;//b_\"", "a;//b_")]
    [InlineData("\"A😀B\"+\"日本語\"", "A😀B日本語")]
    [InlineData("\"<#dbl>//;_<#DBL>\"", "\"//;_\"")]
    [InlineData("\"<#cr><#TaB>\"", "\r\n\t")]
    [InlineData("\"<#UNKNOWN>\"", "<#UNKNOWN>")]
    [InlineData("\"<#DBL><#DBL>\"", "\"\"")]
    public async Task StringContentsDoNotBecomeSourceSyntax(string expression,
        string expected)
    {
        Assert.Equal([expected], await Run("PRINT " + expression + " // ignored;PRINT 9"));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task ContinuationCommentAndMultipleStatementsCompose(string newline)
    {
        Assert.Equal(["3", "a;//b", "after"], await Run(string.Join(newline,
            "// header", "x=\"2\" * _ // ignored ; PRINT 99", "1 + _",
            "1; ; PRINT x;PRINT \"a;//b\" // tail", "PRINT \"after\"")));
    }

    [Fact]
    public async Task MultipleStatementsDoNotExtendInlineIfBody()
    {
        Assert.Equal(["second"], await Run(
            "IF FALSE THEN PRINT \"first\";PRINT \"second\""));
    }

    [Theory]
    [InlineData("PRINT \"closed\"; PRINT 1/0", 1)]
    [InlineData("// header\nPRINT 1 + _\n1/0", 2)]
    public async Task LogicalStatementErrorUsesStartingPhysicalLine(string source, int line)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => Run(source));
        Assert.Equal(line, error.Line);
    }

    [Theory]
    [InlineData("// header\nPRINT \"unterminated", 2)]
    [InlineData("PRINT \"// no comment", 1)]
    [InlineData("PRINT 1 + _ // tail", 1)]
    public void InvalidSourceIsRejectedWithPhysicalLine(string source, int line)
    {
        ScriptException error = Assert.Throws<ScriptException>(() => ScriptCompiler.Compile(source));
        Assert.Equal(line, error.Line);
    }

    [Theory]
    [InlineData("$ff", "255")]
    [InlineData("$7FFFFFFF", "2147483647")]
    [InlineData("$80000000", "2147483648")]
    [InlineData("$FFFFFFFF", "4294967295")]
    [InlineData("-$FFFFFFFF", "-4294967295")]
    [InlineData("VAL(\"$FFFFFFFF\")", "-999999")]
    public void HexLiteralsKeepTheirRangeWhileValRejectsHexText(
        string expression, string expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Display());
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$10000000000000000")]
    [InlineData("$FG")]
    public void InvalidHexIsRejectedAtCompileTime(string expression)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("// header\nPRINT " + expression));
        Assert.Equal(2, error.Line);
    }
}
