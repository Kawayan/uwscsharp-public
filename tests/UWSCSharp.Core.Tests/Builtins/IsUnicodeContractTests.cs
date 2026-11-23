using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class IsUnicodeContractTests
{
    [Theory]
    [InlineData("ISUNICODE(\"\")", 0)]
    [InlineData("ISUNICODE(\"ASCII\")", 0)]
    [InlineData("ISUNICODE(\"日本語\")", 0)]
    [InlineData("ISUNICODE(\"ｶﾅ\")", 0)]
    [InlineData("ISUNICODE(\"髙①\")", 0)]
    [InlineData("ISUNICODE(\"😀\")", 1)]
    [InlineData("ISUNICODE(\"𠮷\")", 1)]
    [InlineData("ISUNICODE(\"é\")", 1)]
    [InlineData("ISUNICODE(\"e\"+CHR($301))", 1)]
    [InlineData("ISUNICODE(CHR(0))", 0)]
    [InlineData("ISUNICODE(\"a\"+CHR(0)+\"😀\")", 1)]
    [InlineData("ISUNICODE(CHR($E000))", 0)]
    [InlineData("ISUNICODE(CHR($FFFF))", 1)]
    [InlineData("ISUNICODE(EMPTY)", 0)]
    [InlineData("ISUNICODE(TRUE)", 0)]
    [InlineData("ISUNICODE(123.5)", 0)]
    [InlineData("iSuNiCoDe(\"ASCII\")", 0)]
    public void FixedCp932EncodingDeterminesThePredicate(string expression, int expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Number);
    }

    [Theory]
    [InlineData("¥", 1)]
    [InlineData("\\", 0)]
    [InlineData("‾", 1)]
    [InlineData("~", 0)]
    [InlineData("〜", 1)]
    [InlineData("～", 0)]
    [InlineData("−", 1)]
    [InlineData("－", 0)]
    [InlineData("¢", 1)]
    [InlineData("￠", 0)]
    [InlineData("£", 1)]
    [InlineData("￡", 0)]
    [InlineData("¬", 1)]
    [InlineData("￢", 0)]
    public void BestFitAlternativesAreNotSilentlyAccepted(string text, int expected)
    {
        Assert.Equal(expected, Predicate(Value.String(text)));
    }

    [Fact]
    public void InvalidUtf16AndSupplementaryPairsReturnTrueWithoutMutation()
    {
        string[] inputs = ["\ud800", "\udc00", "a\ud800b", "\udc00\ud800",
            "\ud800\udc00", "\udbff\udfff"];
        foreach (string input in inputs)
        {
            Assert.Equal(1, Predicate(Value.String(input)));
        }
    }

    [Fact]
    public void FullFiniteRangeControlsAndLongInputsAreSupported()
    {
        Assert.Equal(0, Predicate(Value.Numeric(double.MaxValue)));
        Assert.Equal(0, Predicate(Value.Numeric(double.MinValue)));
        Assert.Equal(0, Predicate(Value.Numeric(-0d)));
        string controls = string.Concat(Enumerable.Range(0, 33).Select(char.ConvertFromUtf32));
        Assert.Equal(0, Predicate(Value.String(controls)));
        string text = new('あ', 1_000_001);
        Assert.Equal(0, Predicate(Value.String(text)));
        Assert.Equal(1, Predicate(Value.String(text + "😀")));
    }

    [Theory]
    [InlineData("", "0")]
    [InlineData("OPTION SAMESTR\n", "0")]
    public async Task SameStringDoesNotAlterTheEncoding(string option, string expected)
    {
        Assert.Equal([expected], await Run(option + "PRINT ISUNICODE(\"日本語\")"));
    }

    [Fact]
    public void EncodableInputDoesNotAllocateAnOutputByteArray()
    {
        var input = Value.String(new string('あ', 1_000_001));
        Assert.Equal(0, Predicate(Value.String("日本語")));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int result = Predicate(input);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, result);
        Assert.InRange(allocated, 0, 65536);
    }

    [Fact]
    public void InvalidKindsAndNonfiniteValuesAreErrorsNotUnicodeDetection()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), new(ValueKind.Omitted), Value.Nothing,
            Value.FromError(5), Value.FromObject(new object()),
            Value.FromArray(array), Value.FromHashTable(table),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Predicate(value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData("ISUNICODE(NULL)")]
    [InlineData("ISUNICODE(NOTHING)")]
    public async Task InvalidTypesAreCatchable(string expression)
    {
        Assert.Equal(["caught"], await Run("TRY\nPRINT " + expression
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY"));
    }

    [Theory]
    [InlineData("ISUNICODE()")]
    [InlineData("ISUNICODE(,)")]
    [InlineData("ISUNICODE(Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndErrorsKeepTheirLine()
    {
        Assert.Equal(["tick", "0"], await Run("PRINT ISUNICODE(Tick())\n"
            + "FUNCTION Tick()\nPRINT \"tick\"\nRESULT=\"日本語\"\nFEND"));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT ISUNICODE(NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT ISUNICODE(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"a\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static int Predicate(Value input) =>
        (int)BuiltinFunctions.Evaluate("ISUNICODE", [input]).Number;
}
