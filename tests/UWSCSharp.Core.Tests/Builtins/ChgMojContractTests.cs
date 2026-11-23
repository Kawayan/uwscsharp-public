using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ChgMojContractTests
{
    [Theory]
    [InlineData("CHGMOJ(\"abc\",\"b\",\"x\")", "axc")]
    [InlineData("CHGMOJ(\"AbAb\",\"ab\",\"x\")", "xx")]
    [InlineData("CHGMOJ(\"aaaa\",\"aa\",\"x\")", "xx")]
    [InlineData("CHGMOJ(\"aaa\",\"aa\",\"x\")", "xa")]
    [InlineData("CHGMOJ(\"abababa\",\"aba\",\"x\")", "xbx")]
    [InlineData("CHGMOJ(\"a\",\"a\",\"aa\")", "aa")]
    [InlineData("CHGMOJ(\"abc\",\"z\",\"x\")", "abc")]
    [InlineData("CHGMOJ(\"\",\"a\",\"x\")", "")]
    [InlineData("CHGMOJ(\"abc\",\"\",\"x\")", "abc")]
    [InlineData("CHGMOJ(\"abc\",\"b\",\"\")", "ac")]
    [InlineData("CHGMOJ(\"abc\",EMPTY,\"x\")", "abc")]
    [InlineData("CHGMOJ(\"abc\",\"b\",EMPTY)", "ac")]
    [InlineData("CHGMOJ(EMPTY,\"b\",\"x\")", "")]
    [InlineData("CHGMOJ(\"A😀B😀\",\"😀\",\"x\")", "AxBx")]
    [InlineData("CHGMOJ(12321,2,9)", "19391")]
    [InlineData("CHGMOJ(TRUE,1,2)", "2")]
    [InlineData("CHGMOJ(\"σς\",\"σ\",\"x\")", "xx")]
    [InlineData("CHGMOJ(\"İi\",\"i\",\"x\")", "İx")]
    [InlineData("CHGMOJ(\"ßSS\",\"SS\",\"x\")", "ßx")]
    [InlineData("CHGMOJ(\"e\"+CHR($301),\"é\",\"x\")", "e\u0301")]
    [InlineData("cHgMoJ(\"ＡA\",\"A\",\"x\")", "Ａx")]
    public void ReplacementsAreLeftToRightNonoverlappingAndLiteral(string expression, string expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedBoundaryRulesPreservePairsAndFindIsolatedUnits(bool sameString)
    {
        const string pair = "\ud800\udc00";
        Assert.Equal(pair, StringOperations.Replace(pair, "\ud800", "x", sameString));
        Assert.Equal(pair, StringOperations.Replace(pair, "\udc00", "x", sameString));
        Assert.Equal("x", StringOperations.Replace(pair, pair, "x", sameString));
        Assert.Equal(pair + "x", StringOperations.Replace(pair + "\udc00", "\udc00", "x", sameString));
        Assert.Equal("x" + pair, StringOperations.Replace("\udc00" + pair, "\udc00", "x", sameString));
        Assert.Equal("a" + pair, StringOperations.Replace("a" + pair, "a\ud800", "x", sameString));
        Assert.Equal(pair, StringOperations.Replace("\ud800a\udc00", "a", "", sameString));
        Assert.Equal("\udc00", StringOperations.Replace("a", "a", "\udc00", sameString));
        Assert.Equal("axb", StringOperations.Replace("a\0b", "\0", "x", sameString));
    }

    [Fact]
    public void ReplacementTextIsNotRescannedOrInterpretedAsRegex()
    {
        Assert.Equal("$1\\$1\\", StringOperations.Replace("aA", "a", "$1\\"));
        Assert.Equal("aaaa", StringOperations.Replace("aa", "a", "aa"));
        string text = new('a', 20);
        Assert.Same(text, StringOperations.Replace(text, "z", "b"));
        Assert.Same(text, StringOperations.Replace(text, "", "b"));
    }

    [Fact]
    public async Task ImpossibleOutputLengthIsRejectedBeforeAllocation()
    {
        string text = new('a', 65536);
        string replacement = new('b', 32768);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            StringOperations.Replace(text, "a", replacement));
        Assert.Contains("長さの範囲", error.Message);
        Assert.Equal(["caught"], await Run("TRY\nPRINT CHGMOJ(FORMAT(\"a\",65536),"
            + "\"a\",FORMAT(\"b\",32768))\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Fact]
    public void OutputAboveOneMillionUnitsHasNoIndependentLimit()
    {
        string text = new('a', 500001);
        string result = StringOperations.Replace(text, "a", "bc");
        Assert.Equal(1000002, result.Length);
        Assert.StartsWith("bcbc", result);
        Assert.EndsWith("bcbc", result);
        Assert.Equal(500001, text.Length);
    }

    [Theory]
    [InlineData("", "xx")]
    [InlineData("OPTION SAMESTR\n", "Ax")]
    public async Task SameStringIsProgramScopedAndAliasesShareImplementation(string option, string expected)
    {
        Assert.Equal([expected, expected], await Run(option
            + "PRINT CHGMOJ(\"Aa\",\"a\",\"x\")\nPRINT REPLACE(\"Aa\",\"a\",\"x\")"));
        Assert.Equal(["xx"], await Run("PRINT CHGMOJ(\"Aa\",\"a\",\"x\")"));
    }

    [Fact]
    public void InvalidKindsAndNonfiniteValuesDoNotMutateInputs()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [Value.Nothing, Value.FromError(5), new(ValueKind.Null),
            new(ValueKind.Omitted), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Replace(value, Value.String("a"), Value.String("x")));
            Assert.Throws<InvalidOperationException>(() => Replace(Value.String("a"), value, Value.String("x")));
            Assert.Throws<InvalidOperationException>(() => Replace(Value.String(""), Value.String(""), value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("CHGMOJ()")]
    [InlineData("CHGMOJ(Tick())")]
    [InlineData("CHGMOJ(Tick(),Tick())")]
    [InlineData("CHGMOJ(,Tick(),Tick())")]
    [InlineData("CHGMOJ(Tick(),,Tick())")]
    [InlineData("CHGMOJ(Tick(),Tick(),)")]
    [InlineData("CHGMOJ(Tick(),Tick(),Tick(),Tick())")]
    [InlineData("REPLACE(Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunOnceAndErrorsKeepTheirLine()
    {
        Assert.Equal(["1", "2", "3", "1", "3"], await Run(
            "PUBLIC calls=0\nPRINT CHGMOJ(Tick(),Tick(),Tick())\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nPRINT calls\nRESULT=calls\nFEND"));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT CHGMOJ(\"a\",\"a\",NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT CHGMOJ(Tick(),\"a\",\"x\")\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"a\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static string Replace(Value text, Value from, Value to) =>
        BuiltinFunctions.Evaluate("CHGMOJ", [text, from, to]).Text;
}
