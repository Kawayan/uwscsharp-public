using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class StringParameterAuditTests
{
    [Theory]
    [InlineData("COPY(\"A😀BC\",\"2\",\"2\")", "😀B")]
    [InlineData("COPY(\"abc\",\"0\",\"2\")", "ab")]
    [InlineData("COPY(\"abc\",\"2\",\"-1\")", "")]
    [InlineData("POS(\"aa\",\"aaaa\",\"-2\")", "2")]
    [InlineData("POS(\"B\",\"A😀B\",\"0\")", "3")]
    [InlineData("TRIM(\"　a　\",\"1\")", "a")]
    [InlineData("TRIM(\"　a　\",\"0\")", "　a　")]
    [InlineData("CHR(\"128512\")", "😀")]
    [InlineData("FORMAT(12.5,\"6\",\"1\")", "  12.5")]
    [InlineData("FORMAT(255,\"4\",\"-1\")", "  FF")]
    [InlineData("FORMAT(\"-\",\"3\")", "---")]
    [InlineData("FORMAT(\"😀\",\"2\")", "😀")]
    [InlineData("JOIN(SPLIT(\"a,b,c\",\",\"),\"|\",\"0\",\"1\",\"99\")", "b|c")]
    [InlineData("JOIN(SLICE(SPLIT(\"a,b,c\",\",\"),\"1\",\"2\"),\",\")", "b,c")]
    [InlineData("JOIN(SPLIT(\"1,,2\",\",\",\"1\",\"1\"),\"|\")", "1|2")]
    public void StringOperationsConvertNumericParameters(string expression, string expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Display());
    }

    [Theory]
    [InlineData("LENGTH(\"A😀B\")", "3")]
    [InlineData("LENGTHS(\"A😀B\")", "4")]
    [InlineData("ASC(\"😀\")", "128512")]
    [InlineData("ISUNICODE(\"😀\")", "1")]
    [InlineData("REPLACE(\"AbAb\",\"ab\",\"x\")", "xx")]
    [InlineData("CHGMOJ(\"AbAb\",\"ab\",\"x\")", "xx")]
    public void UnicodeAndDefaultCaseRulesRemainDistinct(string expression, string expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Display());
    }

    [Theory]
    [InlineData("COPY(\"text\",\"bad\")")]
    [InlineData("POS(\"t\",\"text\",\"1e+\")")]
    [InlineData("TRIM(\"text\",\"bad\")")]
    [InlineData("CHR(\"1114112\")")]
    [InlineData("FORMAT(12,\"-1\")")]
    [InlineData("FORMAT(12,\"2\",\"21\")")]
    public async Task InvalidNumericParametersKeepSourceLine(string expression)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("// heading\nPRINT " + expression, _ => { }));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task TokenConvertsFlagsAndUpdatesOnlyTheRemainder()
    {
        Assert.Equal(["\"a,b\"", "c"], await Run("""
            source=",,"+CHR(34)+"a,b"+CHR(34)+",,c"
            PRINT TOKEN(",",source,"1","1")
            PRINT source
            """));
    }

    [Theory]
    [InlineData("\"bad\"")]
    public async Task TokenFlagFailureDoesNotWriteBack(string flag)
    {
        Assert.Equal(["left,right"], await Run("source=\"left,right\"\nTRY\n"
            + "TOKEN(\",\",source," + flag + ")\nEXCEPT\nPRINT source\nENDTRY"));
    }
}
