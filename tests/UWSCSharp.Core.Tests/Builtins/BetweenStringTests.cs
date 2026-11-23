using Xunit;

namespace UWSCSharp.Core.Tests;

public class BetweenStringTests
{
    [Theory]
    [InlineData("A1CA2CA3C", "A", "C", 0, false, "1")]
    [InlineData("A1CA2CA3C", "A", "C", -1, false, "3")]
    [InlineData("A1CA2CA3C", "A", "C", -2, false, "2")]
    [InlineData("A1CA2CA3C", "A", "C", -3, false, "1")]
    [InlineData("A1A2A3A", "A", "A", -2, false, "1")]
    [InlineData("A1A2A3A", "A", "A", -2, true, "2")]
    [InlineData("A1A2A3A", "A", "A", 2, false, "3")]
    [InlineData("A1A2A3A", "A", "A", 2, true, "2")]
    [InlineData("A1A2A3A", "A", "A", 0, true, "1")]
    [InlineData("prefix[日本😀]suffix", "[", "]", -1, false, "日本😀")]
    [InlineData("xxBEGINbodyENDyy", "begin", "end", -1, false, "body")]
    [InlineData("AC", "A", "C", -1, false, "")]
    [InlineData("", "", "", -1, false, "")]
    [InlineData("abc", "", "", -1, false, "abc")]
    [InlineData("AxxCyy", "", "C", -1, false, "Axx")]
    [InlineData("xxAyyC", "A", "", -1, false, "yyC")]
    public void FindsRequestedOccurrence(string text, string before,
        string after, int occurrence, bool countFromStart, string expected)
    {
        Value actual = BuiltinFunctions.Evaluate("BETWEENSTR",
            [Value.String(text), Value.String(before), Value.String(after),
                Value.Numeric(occurrence), Value.Numeric(countFromStart ? 1 : 0)]);

        Assert.Equal(ValueKind.String, actual.Kind);
        Assert.Equal(expected, actual.Text);
    }

    [Theory]
    [InlineData("A1C", "A", "C", -2)]
    [InlineData("abc", "missing", "c", -1)]
    [InlineData("abc", "a", "missing", -1)]
    [InlineData("CA", "A", "C", -1)]
    [InlineData("abc", "", "", -2)]
    [InlineData("A1C", "A", "C", int.MinValue)]
    public void MissingOccurrenceReturnsEmpty(string text, string before,
        string after, int occurrence)
    {
        Value actual = BuiltinFunctions.Evaluate("BETWEENSTR",
            [Value.String(text), Value.String(before), Value.String(after),
                Value.Numeric(occurrence)]);

        Assert.Equal(ValueKind.Empty, actual.Kind);
    }

    [Fact]
    public void SameStringControlsDelimiterComparison()
    {
        Value[] arguments = [Value.String("aBodyc"), Value.String("A"),
            Value.String("C"), Value.Numeric(-1)];

        Assert.Equal("Body", BuiltinFunctions.Evaluate(
            "BETWEENSTR", arguments).Text);
        Assert.Equal(ValueKind.Empty, BuiltinFunctions.Evaluate(
            "BETWEENSTR", arguments, sameString: true).Kind);
    }

    [Theory]
    [InlineData("BETWEENSTR(\"abc\",,,0)", "abc")]
    [InlineData("BETWEENSTR(\"abc\",,,-1)", "abc")]
    [InlineData("BETWEENSTR(\"A1CA2C\",\"A\",\"C\",\"-2\")", "1")]
    public void ScriptArgumentsReachSearch(string expression, string expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Text);
    }

    [Theory]
    [InlineData("", "Body")]
    [InlineData("OPTION SAMESTR\n", "")]
    public async Task ProgramOptionReachesReverseSearch(
        string option, string expected)
    {
        Assert.Equal([expected], await ControlFlowTests.Run(option + """
            DIM found = BETWEENSTR("aBodyc", "A", "C", -1)
            PRINT found
            """));
    }
}
