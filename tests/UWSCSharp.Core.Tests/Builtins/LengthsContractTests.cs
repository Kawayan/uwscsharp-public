using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class LengthsContractTests
{
    [Theory]
    [InlineData("LENGTHS(\"\")", 0)]
    [InlineData("LENGTHS(\"ABC\")", 3)]
    [InlineData("LENGTHS(\"日本語\")", 3)]
    [InlineData("LENGTHS(\"ｶﾅ\")", 2)]
    [InlineData("LENGTHS(\"😀\")", 2)]
    [InlineData("LENGTHS(\"A😀B\")", 4)]
    [InlineData("LENGTHS(\"𠮷\")", 2)]
    [InlineData("LENGTHS(\"👨‍👩‍👧‍👦\")", 11)]
    [InlineData("LENGTHS(\"🇯🇵\")", 4)]
    [InlineData("LENGTHS(\"e\"+CHR($301))", 2)]
    [InlineData("LENGTHS(CHR(0))", 1)]
    [InlineData("LENGTHS(\"a\"+CHR(0)+\"😀\")", 4)]
    [InlineData("LENGTHS(CHR($FFFF))", 1)]
    [InlineData("LENGTHS(EMPTY)", 0)]
    [InlineData("LENGTHS(TRUE)", 1)]
    [InlineData("LENGTHS(FALSE)", 1)]
    [InlineData("LENGTHS(-0)", 2)]
    [InlineData("LENGTHS(123.5)", 5)]
    [InlineData("LENGTHS(1e20)", 5)]
    [InlineData("LENGTHS(\"NaN\")", 3)]
    [InlineData("LENGTHS(\"Infinity\")", 8)]
    [InlineData("LENGTHS(\"$41\")", 3)]
    [InlineData("lEnGtHs(\"A😀B\")", 4)]
    public void Utf16UnitsAreCountedWithoutNormalization(string expression, int expected)
    {
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression).Number);
    }

    [Fact]
    public void InvalidUnitsAndValidPairsKeepTheirStorageLength()
    {
        (string Text, int Expected)[] cases =
        [
            ("\ud800", 1), ("\udc00", 1), ("\ud800A\udc00", 3),
            ("\udc00\ud800", 2), ("\ud800\udc00", 2), ("\udbff\udfff", 2)
        ];
        foreach ((string text, int expected) in cases)
        {
            var input = Value.String(text);
            Assert.Equal(expected, Count(input));
            Assert.Same(text, input.Text);
        }
    }

    [Fact]
    public void AllBmpUnitsAndLongTextHaveNoIndependentLimit()
    {
        string bmp = string.Concat(Enumerable.Range(0, 65536).Select(c => (char)c));
        Assert.Equal(65536, Count(Value.String(bmp)));
        string text = new('あ', 1_000_001);
        Assert.Equal(1_000_001, Count(Value.String(text)));
        Assert.Equal(1_000_003, Count(Value.String(text + "😀")));
        Assert.Equal(23, Count(Value.Numeric(double.MaxValue)));
        Assert.Equal(24, Count(Value.Numeric(double.MinValue)));
    }

    [Fact]
    public void InvalidKindsAndNonfiniteNumbersDoNotBecomeText()
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
            Assert.Throws<InvalidOperationException>(() => Count(value));
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public async Task CollectionsAreRejectedAndLengthRetainsItsCollectionMeaning()
    {
        Assert.Equal(["caught", "caught", "3", "0"], await Run("""
            DIM a[2]=1,2,3
            HASHTBL h
            TRY
                PRINT LENGTHS(a)
            EXCEPT
                PRINT "caught"
            ENDTRY
            TRY
                PRINT LENGTHS(h)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT LENGTH(a)
            PRINT LENGTH(h)
            """));
    }

    [Theory]
    [InlineData("")]
    [InlineData("OPTION SAMESTR\n")]
    public async Task SameStringDoesNotChangeTheCount(string option)
    {
        Assert.Equal(["4"], await Run(option + "PRINT LENGTHS(\"A😀B\")"));
    }

    [Theory]
    [InlineData("LENGTHS()")]
    [InlineData("LENGTHS(,)")]
    [InlineData("LENGTHS(Tick(),Tick())")]
    [InlineData("LENGTHS(,Tick())")]
    public async Task InvalidArityPreventsArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentRunsOnceAndTypeErrorsKeepTheirLine()
    {
        Assert.Equal(["tick", "2"], await Run("PRINT LENGTHS(Tick())\n"
            + "FUNCTION Tick()\nPRINT \"tick\"\nRESULT=\"😀\"\nFEND"));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT LENGTHS(NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT LENGTHS(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"a\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static int Count(Value input) =>
        (int)BuiltinFunctions.Evaluate("LENGTHS", [input]).Number;
}
