using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class StringLiteralTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(4096)]
    public async Task ConsecutiveQuotesKeepAllInteriorCharacters(int count)
    {
        string expression = new('"', count);
        string expected = new('"', count - 2);
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Text);
        var output = new List<string>();
        await new Interpreter().RunAsync("PRINT " + expression, output.Add);
        Assert.Equal([expected], output);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ClosingQuoteRunKeepsAllButLastQuote(int count)
    {
        string expression = "\"日本語😀" + new string('"', count);
        string expected = "日本語😀" + new string('"', count - 1);
        var output = new List<string>();
        await new Interpreter().RunAsync("PRINT " + expression, output.Add);
        Assert.Equal([expected], output);
    }

    [Theory]
    [InlineData("PRINT \"\"\" // ignored", "\"")]
    [InlineData("PRINT \"\"\";PRINT \"tail\"", "\"", "tail")]
    [InlineData("PRINT \"\"\" + _\n \"tail\"", "\"tail")]
    [InlineData("IF \"\"\" = CHR(34) THEN PRINT \"yes\"", "yes")]
    [InlineData("IF \"THEN\"\" = \"THEN<#DBL>\" THEN PRINT \"yes\"", "yes")]
    [InlineData("CONST text=\"\"\"\nPRINT text", "\"")]
    [InlineData("DIM text=\"\"\"\nPRINT LENGTH(text)", "1")]
    [InlineData("PRINT Echo()\nFUNCTION Echo(value=\"\"\")\nRESULT=value\nFEND", "\"")]
    [InlineData("DIM values[]=\"\"\",\"x\"\nPRINT JOIN(values,\"/\")", "\"/x")]
    [InlineData("OPTION SPECIALCHAR\nPRINT \"<#DBL>\"\"", "<#DBL>\"")]
    public async Task AllSourceConsumersUseTheSameLiteralBoundary(
        string source, params string[] expected)
    {
        var output = new List<string>();
        await new Interpreter().RunAsync(source, output.Add);
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData("PRINT \"")]
    [InlineData("PRINT \"a\" \"b\"")]
    [InlineData("PRINT \"\"\"a\"")]
    [InlineData("PRINT \"a\"\"b\"")]
    public async Task UnterminatedAndAdjacentLiteralsFailBeforeOutput(string source)
    {
        var output = new List<string>();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("\n" + source, output.Add));
        Assert.Equal(2, error.Line);
        Assert.Empty(output);
    }
}
