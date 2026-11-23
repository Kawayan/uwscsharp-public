using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class NumericStringAuditTests
{
    [Theory]
    [InlineData("-\"2.5\"", -2.5)]
    [InlineData("-\"$FFFFFFFF\"", 1)]
    [InlineData("-\"1e2\"", -100)]
    [InlineData("!\"0\"", 1)]
    [InlineData("!\"-0.5\"", 0)]
    public void UnaryNegationAndZeroComparisonConvertStrings(string expression, double expected)
    {
        Assert.Equal(Value.Numeric(expected), ExpressionEvaluator.Evaluate(expression));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("abc")]
    public void UnaryPlusKeepsStringKind(string text)
    {
        Assert.Equal(Value.String(text), ExpressionEvaluator.Evaluate("+\"" + text + "\""));
    }

    [Fact]
    public void UnaryPlusRetainsNullAndImmutableArrayValues()
    {
        Assert.Equal(new Value(ValueKind.Null), ExpressionEvaluator.Evaluate("+NULL"));
        var array = UwscArray.FromValues([Value.Numeric(2)]);
        Value value = ExpressionEvaluator.Evaluate("+items",
            new Dictionary<string, Value> { ["items"] = Value.FromArray(array) });
        Assert.Same(array, value.AsArray());
    }

    [Theory]
    [InlineData("-\"bad\"")]
    [InlineData("!\"1e999\"")]
    [InlineData("-NULL")]
    public void UnaryNumericFailuresRemainDiagnosed(string expression)
    {
        Assert.Throws<InvalidOperationException>(() => ExpressionEvaluator.Evaluate(expression));
    }

    [Theory]
    [InlineData("IF \"0\" THEN PRINT 9\nIF \"-0.5\" THEN PRINT 1", "1")]
    [InlineData("IFB \"1e2\"\nPRINT 1\nENDIF", "1")]
    [InlineData("text=\"1\"\nWHILE text\nPRINT 1\ntext=\"0\"\nWEND", "1")]
    [InlineData("REPEAT\nPRINT 1\nUNTIL \"$FF\"", "1")]
    public async Task ConditionsCompareNumericStringsWithZero(string script, string expected)
    {
        List<string> output = [];
        await new Interpreter().RunAsync(script, output.Add);
        Assert.Equal([expected], output);
    }

    [Theory]
    [InlineData("IF \"bad\" THEN PRINT 1", 1)]
    [InlineData("WHILE NULL\nWEND", 1)]
    [InlineData("REPEAT\nUNTIL \"1e999\"", 2)]
    public async Task InvalidConditionsRetainTheirSourceLine(string script, int line)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync(script, _ => { }));
        Assert.Equal(line, error.Line);
    }

    [Theory]
    [InlineData("FOR i=\"1\" TO \"3\" STEP \"1\"\nPRINT i\nNEXT", "1,2,3")]
    [InlineData("FOR i=\"3\" TO \"1\" STEP \"-1\"\nPRINT i\nNEXT", "3,2,1")]
    [InlineData("FOR i=\"0.5\" TO \"1\" STEP \"0.5\"\nPRINT i\nNEXT", "0.5,1")]
    [InlineData("FOR i=1 TO 3\nPRINT i\ni=\"3\"\nNEXT\nPRINT i", "1,4")]
    public async Task ForLoopConvertsNumericStringsAtEveryNumericBoundary(string script, string expected)
    {
        List<string> output = [];
        await new Interpreter().RunAsync(script, output.Add);
        Assert.Equal(expected, string.Join(",", output));
    }

    [Theory]
    [InlineData("FOR i=\"bad\" TO 1\nNEXT")]
    [InlineData("FOR i=1 TO \"bad\"\nNEXT")]
    [InlineData("FOR i=3 TO 1 STEP \"0\"\nNEXT")]
    public async Task InvalidForConversionReportsTheLoopHeader(string script)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("// heading\n" + script, _ => { }));
        Assert.Equal(2, error.Line);
    }

    [Theory]
    [InlineData("\"6\" AND 3", 2)]
    [InlineData("4 OR \"1\"", 5)]
    [InlineData("\"7\" XOR \"3\"", 4)]
    [InlineData("\"6.9\" AND 3", 2)]
    [InlineData("\"$FFFFFFFF\" AND 3", 3)]
    [InlineData("\"1e2\" OR 1", 101)]
    [InlineData("EMPTY OR \"2\"", 2)]
    public void BitwiseOperatorsConvertNumericStringsBeforeTruncation(string expression, double expected)
    {
        Assert.Equal(Value.Numeric(expected), ExpressionEvaluator.Evaluate(expression));
    }

    [Theory]
    [InlineData("\"bad\" AND 1")]
    [InlineData("\"1e999\" OR 1")]
    [InlineData("\"9223372036854775808\" XOR 1")]
    [InlineData("NULL AND 1")]
    public async Task InvalidBitwiseConversionPreservesScriptLine(string expression)
    {
        Assert.Throws<InvalidOperationException>(() => ExpressionEvaluator.Evaluate(expression));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("// heading\nPRINT " + expression, _ => { }));
        Assert.Equal(2, error.Line);
    }
}
