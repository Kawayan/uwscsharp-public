using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ExpressionCapacityTests
{
    [Theory]
    [InlineData(2047, true)]
    [InlineData(2048, false)]
    public void FlatCallKeepsTokenBoundary(int count, bool accepted)
    {
        string arguments = string.Join(",", Enumerable.Repeat("1", count));
        string source = $"PRINT Probe({arguments})";
        if (accepted)
        {
            Assert.NotNull(ScriptCompiler.Compile(source));
        }
        else
        {
            ScriptException error = Assert.Throws<ScriptException>(() =>
                ScriptCompiler.Compile(source));
            Assert.Equal(1, error.Line);
            Assert.Contains("式が長すぎます。", error.Message);
        }
    }

    [Theory]
    [InlineData(2048, true)]
    [InlineData(2049, false)]
    public async Task FlatArithmeticKeepsTokenBoundary(int count, bool accepted)
    {
        string expression = string.Join("+", Enumerable.Repeat("1", count));
        string source = "PRINT " + expression;
        var output = new List<string>();
        if (accepted)
        {
            await new Interpreter().RunAsync(source, output.Add);
            Assert.Equal([count.ToString()], output);
        }
        else
        {
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter().RunAsync(source, output.Add));
            Assert.Contains("式が長すぎます。", error.Message);
            Assert.Empty(output);
        }
    }

    [Theory]
    [InlineData("OR", "TRUE", false, false, 1)]
    [InlineData("AND", "FALSE", false, false, 1)]
    [InlineData("OR", "TRUE", true, false, 0)]
    [InlineData("AND", "FALSE", true, false, 0)]
    [InlineData("OR", "TRUE", true, true, 1)]
    [InlineData("AND", "FALSE", true, true, 1)]
    public async Task LongConditionPreservesShortCircuitBoundary(string op,
        string value, bool shortCircuit, bool grouped, int expectedCalls)
    {
        string expression = string.Join(" " + op + " ",
            Enumerable.Repeat(value, 2000)) + " " + op + " Probe()";
        if (grouped)
        {
            expression = "(" + expression + ")";
        }

        var output = new List<string>();
        await new Interpreter().RunAsync($$"""
            {{(shortCircuit ? "OPTION SHORTCIRCUIT" : "")}}
            PUBLIC calls=0
            IF {{expression}} THEN calls=calls
            PRINT calls
            FUNCTION Probe()
                calls=calls+1
                RESULT=TRUE
            FEND
            """, output.Add);
        Assert.Equal([expectedCalls.ToString()], output);
    }

    [Fact]
    public async Task LongArithmeticPreservesCallOrderAndLeftAssociativity()
    {
        string expression = string.Join("-", Enumerable.Range(0, 512)
            .Select(index => $"Probe({index})"));
        var output = new List<string>();
        await new Interpreter().RunAsync($$"""
            PUBLIC nextValue=0
            PRINT {{expression}}
            PRINT nextValue
            FUNCTION Probe(value)
                IF value<>nextValue THEN RESULT=1/0
                nextValue=nextValue+1
                RESULT=value
            FEND
            """, output.Add);
        Assert.Equal(["-130816", "512"], output);
    }

}
