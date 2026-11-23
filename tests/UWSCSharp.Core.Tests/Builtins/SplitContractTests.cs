using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SplitContractTests
{
    [Theory]
    [InlineData("", " ", false, new string[0])]
    [InlineData(" ", " ", false, new[] { "", "" })]
    [InlineData(" a  b ", " ", false, new[] { "", "a", "", "b", "" })]
    [InlineData(" a  b ", " ", true, new[] { "a", "b" })]
    [InlineData("a,b,,c,", ",", false, new[] { "a", "b", "", "c", "" })]
    [InlineData("a,b,,c,", ",", true, new[] { "a", "b", "c" })]
    [InlineData("aaa", "aa", false, new[] { "", "a" })]
    [InlineData("a::b::::c::", "::", false, new[] { "a", "b", "", "c", "" })]
    [InlineData("a,b;c", ",;", false, new[] { "a,b;c" })]
    [InlineData("aAxa", "a", false, new[] { "", "Ax", "" })]
    [InlineData("a b", "", false, new[] { "a", "b" })]
    [InlineData("a\tb c", " ", false, new[] { "a\tb", "c" })]
    [InlineData("A😀B😀C", "😀", false, new[] { "A", "B", "C" })]
    [InlineData("e\u0301x", "é", false, new[] { "e\u0301x" })]
    public void LiteralSeparatorsKeepTheirNonoverlappingCaseSensitiveMeaning(
        string text, string separator, bool skipEmpty, string[] expected)
    {
        UwscArray result = ArrayOperations.Split(text, separator, skipEmpty);
        Assert.Equal(1, result.Rank);
        Assert.Equal(0, result.LowerBounds[0]);
        Assert.Equal(expected.Length - 1, result.UpperBounds[0]);
        Assert.Equal(expected.Select(Value.String), result.Values);
    }

    [Theory]
    [InlineData("1e3", 1000)]
    [InlineData("1e+3", 1000)]
    [InlineData("1e-3", 0.001)]
    [InlineData("+2", 2)]
    [InlineData(".5", 0.5)]
    [InlineData("1.", 1)]
    [InlineData("1,456", 1456)]
    [InlineData("-2,345.5", -2345.5)]
    [InlineData("1,234,567", 1234567)]
    [InlineData(" 1,234 ", 1234)]
    [InlineData("1,234e2", 123400)]
    [InlineData("+1,234.", 1234)]
    public void NumericTokensUseFiniteParsingWithStrictGroupedNumbers(string text, double expected)
    {
        UwscArray result = ArrayOperations.Split(text, "|", numeric: true);
        Assert.Equal([Value.Numeric(expected)], result.Values);
    }

    [Theory]
    [InlineData("$41")]
    [InlineData("$FFFFFFFF")]
    [InlineData("0x41")]
    [InlineData("1E")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    [InlineData("1,234e999")]
    [InlineData("12,34")]
    [InlineData("1,,000")]
    [InlineData("1234,567")]
    [InlineData("1\0")]
    [InlineData("１")]
    public void InvalidNumericTokensBecomeStringsBeforeEmptyFiltering(string text)
    {
        Assert.Equal([Value.String("")], ArrayOperations.Split(text, "|", numeric: true).Values);
        Assert.Empty(ArrayOperations.Split(text, "|", true, true).Values);
    }

    [Fact]
    public void NumericKindsSignedZeroAndLongTokensArePreserved()
    {
        UwscArray result = ArrayOperations.Split("1|x|2||-0", "|", numeric: true);
        Assert.Equal([Value.Numeric(1), Value.String(""), Value.Numeric(2),
            Value.String(""), Value.Numeric(-0d)], result.Values);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(result.Get(4).Number));
        string longToken = new('0', 1000);
        Assert.Equal([Value.Numeric(1)], ArrayOperations.Split(
            longToken + "1", "|", numeric: true).Values);
    }

    [Fact]
    public void PairBoundariesAndIsolatedUnitsAreHandledWithoutMutation()
    {
        const string pairText = "A\ud83d\ude00B";
        foreach (string separator in new[] { "\ud83d", "\ude00", "A\ud83d", "\ude00B" })
        {
            Assert.Equal([Value.String(pairText)], ArrayOperations.Split(pairText, separator).Values);
        }

        Assert.Equal([Value.String(""), Value.String("a"), Value.String("")],
            ArrayOperations.Split("\ud800a\ud800", "\ud800").Values);
        Assert.Equal([Value.String("a"), Value.String("b")],
            ArrayOperations.Split("a\0b", "\0").Values);
        Assert.Equal([Value.String("a\0b")], ArrayOperations.Split("a\0b", "|").Values);
        Assert.Equal("A\ud83d\ude00B", pairText);
    }

    [Theory]
    [InlineData("0.5", 3)]
    [InlineData("-0.5", 3)]
    [InlineData("1.5", 2)]
    [InlineData("-1.5", 2)]
    [InlineData("1e20", 2)]
    [InlineData("\"1e20\"", 2)]
    [InlineData("EMPTY", 3)]
    public void FlagsTruncateFiniteValuesWithoutAnInt32Limit(string flag, int expected)
    {
        Value result = ExpressionEvaluator.Evaluate("SPLIT(\"a,,b\",\",\"," + flag + ")");
        Assert.Equal(expected, result.AsArray().Count);
    }

    [Fact]
    public void EveryArgumentRejectsUnsupportedKindsAndNonfiniteNumbers()
    {
        var array = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        Value[] invalid = [new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromObject(new object()), Value.FromArray(array), Value.FromHashTable(table),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            for (int index = 0; index < 4; index++)
            {
                Value[] arguments = [Value.String("x"), Value.String("|"), Value.Empty, Value.Empty];
                arguments[index] = value;
                Assert.Throws<InvalidOperationException>(() => BuiltinFunctions.Evaluate("SPLIT", arguments));
            }
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
        foreach (string text in new[] { "$1", "NaN", "Infinity", "", "1e", "1\0" })
        {
            Assert.Throws<InvalidOperationException>(() => BuiltinFunctions.Evaluate("SPLIT",
                [Value.String(""), Value.Empty, Value.String(text)]));
        }
    }

    [Fact]
    public void ResultLimitIsAppliedAfterFilteringWithoutMaterializingAllFragments()
    {
        string text = new(',', UwscArray.MaximumElements + 1);
        Assert.Empty(ArrayOperations.Split(",", ",", true).Values);
        long before = GC.GetAllocatedBytesForCurrentThread();
        UwscArray filtered = ArrayOperations.Split(text, ",", true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(filtered.Values);
        Assert.InRange(allocated, 0, 65536);
        Assert.Throws<InvalidOperationException>(() => ArrayOperations.Split(text, ","));
        UwscArray maximum = ArrayOperations.Split(text[..(UwscArray.MaximumElements - 1)], ",");
        Assert.Equal(UwscArray.MaximumElements, maximum.Count);
        Assert.Equal(UwscArray.MaximumElements - 1, maximum.UpperBounds[0]);
    }

    [Fact]
    public async Task DefaultsOmissionsFiniteTextAndSameStringKeepTheirContract()
    {
        Assert.Equal(["a|b", "a|b", "-0", "|Ax|", "123|5", ""], await Run("""
            PRINT JOIN(SPLIT("a b",,,),"|")
            PRINT JOIN(SPLIT("a b",EMPTY),"|")
            PRINT JOIN(SPLIT(-0),"|")
            PRINT JOIN(SPLIT("aAxa","a"),"|")
            PRINT JOIN(SPLIT(123.5,"."),"|")
            PRINT JOIN(SPLIT(EMPTY),"|")
            """));
        Assert.Equal(["|Ax|"], await Run(
            "OPTION SAMESTR\nPRINT JOIN(SPLIT(\"aAxa\",\"a\"),\"|\")"));
    }

    [Theory]
    [InlineData("SPLIT()")]
    [InlineData("SPLIT(,Tick())")]
    [InlineData("SPLIT(Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\na="
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunOnceInOrderAndFailureKeepsTheDestination()
    {
        Assert.Equal(["text", "separator", "skip", "numeric", "0", "kept"], await Run("""
            a=SPLIT(Tick("text"),Tick("separator"),Tick("skip"),Tick("numeric"))
            PRINT LENGTH(a)
            a="kept"
            TRY
                a=SPLIT("x",NULL)
            EXCEPT
                PRINT a
            ENDTRY
            FUNCTION Tick(name)
                PRINT name
                RESULT="1"
            FEND
            """));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\na=SPLIT(\"x\",NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("a=SPLIT(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"a\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }
}
