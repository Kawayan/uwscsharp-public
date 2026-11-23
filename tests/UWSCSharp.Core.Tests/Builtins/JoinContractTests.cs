using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class JoinContractTests
{
    [Theory]
    [InlineData(false, "a|||b|0")]
    [InlineData(true, "a|b|0")]
    public void EmptyFilteringDoesNotRemoveZeroOrWhitespace(bool skipEmpty, string expected)
    {
        var array = UwscArray.FromValues([Value.String("a"), Value.String(""),
            Value.Empty, Value.String("b"), Value.Numeric(0)]);
        Assert.Equal(expected, ArrayOperations.Join(array, "|", skipEmpty));
        Assert.Equal(" ", ArrayOperations.Join(
            UwscArray.FromValues([Value.String(" ")]), skipEmpty: true));
        Assert.Equal(Value.Empty, array.Get(2));
    }

    [Theory]
    [InlineData("1.9", "3.9", "b|c|d")]
    [InlineData("-0.9", "1.9", "a|b")]
    [InlineData("-1e20", "1e20", "a|b|c|d")]
    [InlineData("1e20", "1e20", "")]
    [InlineData("0", "-1", "")]
    [InlineData("3", "1", "")]
    public void FiniteRangesTruncateAndClampBeforeConversionToInt32(
        string start, string end, string expected)
    {
        var array = UwscArray.FromValues([Value.String("a"), Value.String("b"),
            Value.String("c"), Value.String("d")]);
        Assert.Equal(expected, Call(Value.FromArray(array), Value.String("|"),
            Value.Empty, Value.String(start), Value.String(end)));
    }

    [Theory]
    [InlineData(0.5, "a||b")]
    [InlineData(-0.5, "a||b")]
    [InlineData(1.5, "a|b")]
    [InlineData(-1.5, "a|b")]
    [InlineData(1e20, "a|b")]
    public void FiniteFlagsHaveNoInt32Limit(double flag, string expected)
    {
        var array = UwscArray.FromValues([Value.String("a"), Value.Empty, Value.String("b")]);
        Assert.Equal(expected, Call(Value.FromArray(array), Value.String("|"), Value.Numeric(flag)));
    }

    [Fact]
    public void MultidimensionalAndNonzeroBoundArraysUseFlatZeroBasedPositions()
    {
        var array = UwscArray.CreateBounded([-2, 5], [-1, 7],
            Enumerable.Range(1, 6).Select(n => Value.Numeric(n)));
        Assert.Equal("1|2|3|4|5|6", ArrayOperations.Join(array, "|"));
        Assert.Equal("2|3|4|5", ArrayOperations.Join(array, "|", start: 1, end: 4));
        Assert.Equal([-2, 5], array.LowerBounds);
        Assert.Equal([-1, 7], array.UpperBounds);
        Assert.Equal(2, array.Rank);
    }

    [Fact]
    public void SelectedElementsUseFiniteTextAndKeepSignedZero()
    {
        var array = UwscArray.FromValues([Value.Numeric(-0d), Value.Numeric(1e20),
            Value.String("NaN"), Value.String("Infinity"), Value.Numeric(1)]);
        Assert.Equal("-0|1E+20|NaN|Infinity|1", ArrayOperations.Join(array, "|"));
        Assert.Equal("-01.51E+201.5NaN1.5Infinity1.51", Call(
            Value.FromArray(array), Value.Numeric(1.5)));
    }

    [Fact]
    public void Utf16UnitsNulAndNewPairsAreCopiedWithoutNormalization()
    {
        var array = UwscArray.FromValues([Value.String("a\0b"),
            Value.String("\ud800"), Value.String("\udc00"), Value.String("e\u0301")]);
        Assert.Equal("a\0b\ud800\udc00e\u0301", ArrayOperations.Join(array, ""));
        Assert.Equal("a\0b\0\ud800\0\udc00\0e\u0301", ArrayOperations.Join(array, "\0"));
        Assert.Equal("\ud800", array.Get(1).Text);
        Assert.Equal("\udc00", array.Get(2).Text);
    }

    [Fact]
    public void UnsupportedSelectedValuesFailButExcludedValuesAreNotConverted()
    {
        Value[] invalid = [new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromObject(new object()), Value.FromArray(UwscArray.FromValues([])),
            Value.FromHashTable(UwscHashTable.Create()), new(ValueKind.Omitted),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            var array = UwscArray.FromValues([value, Value.String("kept")]);
            Assert.Throws<InvalidOperationException>(() => ArrayOperations.Join(array, "|", true));
            Assert.Equal("kept", ArrayOperations.Join(array, "|", start: 1));
            Assert.Equal("", ArrayOperations.Join(array, "|", start: 2));
            Assert.Equal(value, array.Get(0));
        }
    }

    [Fact]
    public void UnsupportedArgumentsFailEvenWhenTheSelectedRangeIsEmpty()
    {
        var array = Value.FromArray(UwscArray.FromValues([]));
        Value[] invalid = [new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromObject(new object()), Value.FromArray(UwscArray.FromValues([])),
            Value.FromHashTable(UwscHashTable.Create()),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            for (int index = 1; index < 5; index++)
            {
                Value[] arguments = [array, Value.String("|"), Value.Empty, Value.Empty,
                    Value.Numeric(int.MaxValue)];
                arguments[index] = value;
                Assert.Throws<InvalidOperationException>(() => Call(arguments));
            }

            if (value.Kind != ValueKind.Array)
            {
                Assert.Throws<InvalidOperationException>(() => Call(value));
            }
        }

        foreach (string text in new[] { "$1", "NaN", "Infinity", "", "1e", "1\0" })
        {
            for (int index = 2; index < 5; index++)
            {
                Value[] arguments = [array, Value.String("|"), Value.Empty, Value.Empty,
                    Value.Numeric(int.MaxValue)];
                arguments[index] = Value.String(text);
                Assert.Throws<InvalidOperationException>(() => Call(arguments));
            }
        }
    }

    [Fact]
    public void OutputLengthIsCheckedWithoutAllocatingTheOversizedResult()
    {
        string text = new('あ', 1_100_000);
        var largeElements = UwscArray.FromValues(Enumerable.Repeat(Value.String(text), 2000));
        var emptyElements = UwscArray.FromValues(Enumerable.Repeat(Value.Empty, 2000));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidOperationException>(() => ArrayOperations.Join(largeElements, ""));
        Assert.Throws<InvalidOperationException>(() => ArrayOperations.Join(emptyElements, text));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 2_000_000);
        Assert.Equal("", ArrayOperations.Join(emptyElements, text, true));
        Assert.Same(text, ArrayOperations.Join(largeElements, "", start: 0, end: 0));
    }

    [Fact]
    public async Task DefaultsOmissionsAndExplicitEmptyValuesAreDistinct()
    {
        Assert.Equal(["a b c", "a b c", "abc", "abc", "a", "a|b|c"], await Run("""
            DIM a[]="a","b","c"
            PRINT JOIN(a)
            PRINT JOIN(a,,,,)
            PRINT JOIN(a,"")
            PRINT JOIN(a,EMPTY)
            PRINT JOIN(a,"|",FALSE,EMPTY,EMPTY)
            PRINT jOiN(a,"|")
            """));
        Assert.Equal(["a|b"], await Run(
            "OPTION SAMESTR\nDIM a[]=\"a\",\"b\"\nPRINT JOIN(a,\"|\")"));
    }

    [Theory]
    [InlineData("JOIN()")]
    [InlineData("JOIN(,Tick())")]
    [InlineData("JOIN(a,Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nDIM a[]=1\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunOnceInOrderAndFailureKeepsTheDestination()
    {
        Assert.Equal(["array", "separator", "skip", "start", "end", "2", "kept"], await Run("""
            DIM a[]=1,2,3
            result=JOIN(Make(),Tick("separator"),Tick("skip"),Tick("start"),Tick("end"))
            PRINT result
            result="kept"
            TRY
                result=JOIN(a,NULL)
            EXCEPT
                PRINT result
            ENDTRY
            FUNCTION Make()
                PRINT "array"
                DIM values[]=1,2,3
                RESULT=values
            FEND
            FUNCTION Tick(name)
                PRINT name
                RESULT=1
            FEND
            """));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("DIM a[]=1\nPRINT JOIN(a,NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT JOIN(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=1\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static string Call(params Value[] arguments) =>
        BuiltinFunctions.Evaluate("JOIN", arguments).Text;
}
