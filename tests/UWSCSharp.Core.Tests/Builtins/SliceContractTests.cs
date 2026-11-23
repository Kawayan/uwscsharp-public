using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SliceContractTests
{
    [Theory]
    [InlineData(0, 3, new[] { 1, 2, 3, 4 })]
    [InlineData(1, 2, new[] { 2, 3 })]
    [InlineData(0, 0, new[] { 1 })]
    [InlineData(-2, 1, new[] { 1, 2 })]
    [InlineData(1, 99, new[] { 2, 3, 4 })]
    [InlineData(3, 1, new int[0])]
    [InlineData(99, 100, new int[0])]
    [InlineData(0, -1, new int[0])]
    public void InclusiveClampedRangesProduceNewZeroBasedArrays(int start, int end, int[] expected)
    {
        var source = UwscArray.FromValues(Enumerable.Range(1, 4).Select(n => Value.Numeric(n)));
        UwscArray result = ArrayOperations.Slice(source, start, end);
        Assert.Equal(expected.Select(n => Value.Numeric(n)), result.Values);
        Assert.Equal(1, result.Rank);
        Assert.Equal([0], result.LowerBounds);
        Assert.Equal([expected.Length - 1], result.UpperBounds);
        Assert.NotSame(source, result);
        Assert.Equal(4, source.Count);
    }

    [Theory]
    [InlineData("1.9", "2.9", new[] { 2, 3 })]
    [InlineData("-0.9", "1.9", new[] { 1, 2 })]
    [InlineData("-1e20", "1e20", new[] { 1, 2, 3, 4 })]
    [InlineData("1e20", "1e20", new int[0])]
    [InlineData("0", "-1e20", new int[0])]
    public void FiniteBoundsTruncateBeforeClamping(string start, string end, int[] expected)
    {
        var source = Value.FromArray(UwscArray.FromValues(
            Enumerable.Range(1, 4).Select(n => Value.Numeric(n))));
        Assert.Equal(expected.Select(n => Value.Numeric(n)), Call(
            source, Value.String(start), Value.String(end)).Values);
    }

    [Fact]
    public void MultidimensionalAndNonzeroBoundsAreFlattenedAndRebased()
    {
        var source = UwscArray.CreateBounded([-2, 5], [-1, 7],
            Enumerable.Range(1, 6).Select(n => Value.Numeric(n)));
        UwscArray result = ArrayOperations.Slice(source, 1, 4);
        Assert.Equal([Value.Numeric(2), Value.Numeric(3), Value.Numeric(4), Value.Numeric(5)],
            result.Values);
        Assert.Equal(1, result.Rank);
        Assert.Equal([0], result.LowerBounds);
        Assert.Equal([3], result.UpperBounds);
        Assert.Equal([-2, 5], source.LowerBounds);
        Assert.Equal([-1, 7], source.UpperBounds);
        Assert.Equal(2, source.Rank);
        Assert.Equal([Value.Numeric(4), Value.Numeric(5), Value.Numeric(6)],
            ArrayOperations.Slice(source.Get(-1).AsArray()).Values);
    }

    [Fact]
    public void EveryValueKindAndRawNumericPayloadAreCopiedWithoutConversion()
    {
        object instance = new();
        var nested = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        double payloadNaN = BitConverter.Int64BitsToDouble(0x7ff8000000000042);
        Value[] values = [Value.Empty, new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromObject(instance), Value.FromArray(nested), Value.FromHashTable(table),
            new(ValueKind.Omitted), Value.String("a\0😀\ud800"), Value.Numeric(-0d),
            new(ValueKind.Number, payloadNaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        var source = UwscArray.FromValues(values);
        UwscArray result = Call(Value.FromArray(source));
        Assert.Equal(values, result.Values);
        Assert.Same(instance, result.Get(4).AsObject());
        Assert.Same(nested, result.Get(5).AsArray());
        Assert.Same(table, result.Get(6).AsHashTable());
        for (int index = 9; index < values.Length; index++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(values[index].Number),
                BitConverter.DoubleToInt64Bits(result.Get(index).Number));
        }

        Assert.Equal("a\0😀\ud800", result.Get(8).Text);
    }

    [Fact]
    public void UpdatingTheReturnedContainerDoesNotChangeTheSource()
    {
        var source = UwscArray.FromValues([Value.Numeric(1), Value.Numeric(2), Value.Numeric(3)]);
        UwscArray result = ArrayOperations.Slice(source, 1, 2);
        UwscArray updated = result.Set([0], Value.Numeric(99));
        Assert.Equal([Value.Numeric(1), Value.Numeric(2), Value.Numeric(3)], source.Values);
        Assert.Equal([Value.Numeric(2), Value.Numeric(3)], result.Values);
        Assert.Equal([Value.Numeric(99), Value.Numeric(3)], updated.Values);
    }

    [Fact]
    public void EmptyAndMaximumSizedInputsUseTheExistingArrayLimit()
    {
        var empty = UwscArray.FromValues([]);
        UwscArray emptyResult = ArrayOperations.Slice(empty);
        Assert.Empty(emptyResult.Values);
        Assert.Equal([-1], emptyResult.UpperBounds);
        Assert.NotSame(empty, emptyResult);
        var maximum = UwscArray.Create([UwscArray.MaximumElements - 1]);
        UwscArray all = ArrayOperations.Slice(maximum);
        Assert.Equal(UwscArray.MaximumElements, all.Count);
        Assert.Equal(UwscArray.MaximumElements - 1, all.UpperBounds[0]);
        Assert.Equal(ValueKind.Empty, all.Get(UwscArray.MaximumElements - 1).Kind);
        Assert.Equal(maximum.Get(UwscArray.MaximumElements - 1),
            all.Get(UwscArray.MaximumElements - 1));
        Assert.Equal(2, ArrayOperations.Slice(maximum, 1, 2).Count);
    }

    [Fact]
    public void InvalidArgumentsAreRejectedEvenForEmptySources()
    {
        var empty = Value.FromArray(UwscArray.FromValues([]));
        Value[] invalid = [new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromObject(new object()), Value.FromArray(UwscArray.FromValues([])),
            Value.FromHashTable(UwscHashTable.Create()),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        foreach (Value value in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Call(empty, value));
            Assert.Throws<InvalidOperationException>(() => Call(empty, Value.Empty, value));
            if (value.Kind != ValueKind.Array)
            {
                Assert.Throws<InvalidOperationException>(() => Call(value));
            }
        }

        foreach (string text in new[] { "$1", "NaN", "Infinity", "", "1e", "1\0" })
        {
            Assert.Throws<InvalidOperationException>(() => Call(empty, Value.String(text)));
            Assert.Throws<InvalidOperationException>(() => Call(empty, Value.Empty, Value.String(text)));
        }

        Assert.Throws<InvalidOperationException>(() => Call(Value.Empty));
        Assert.Throws<InvalidOperationException>(() => Call(Value.Numeric(1)));
        Assert.Throws<InvalidOperationException>(() => Call(Value.String("abc")));
    }

    [Fact]
    public async Task OptionalOmissionsAndExplicitEmptyBoundsRemainDistinct()
    {
        Assert.Equal(["1|2|3", "1|2|3", "1", "2|3", "1|2|3"], await Run("""
            DIM a[]=1,2,3
            PRINT JOIN(SLICE(a),"|")
            PRINT JOIN(SLICE(a,,),"|")
            PRINT JOIN(SLICE(a,EMPTY,EMPTY),"|")
            PRINT JOIN(sLiCe(a,"1","2"),"|")
            b=SLICE(a)
            b[0]=99
            PRINT JOIN(a,"|")
            """));
        Assert.Equal(["1|2"], await Run(
            "OPTION SAMESTR\nDIM a[]=1,2\nPRINT JOIN(SLICE(a),\"|\")"));
    }

    [Theory]
    [InlineData("SLICE()")]
    [InlineData("SLICE(,Tick())")]
    [InlineData("SLICE(a,Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nDIM a[]=1\nTRY\nb="
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunOnceInOrderAndFailureKeepsTheDestination()
    {
        Assert.Equal(["array", "start", "end", "2", "kept"], await Run("""
            b=SLICE(Make(),Tick("start"),Tick("end"))
            PRINT b[0]
            b="kept"
            TRY
                b=SLICE(MakeSilent(),NULL)
            EXCEPT
                PRINT b
            ENDTRY
            FUNCTION Make()
                PRINT "array"
                RESULT=MakeSilent()
            FEND
            FUNCTION MakeSilent()
                DIM values[]=1,2,3
                RESULT=values
            FEND
            FUNCTION Tick(name)
                PRINT name
                RESULT=1
            FEND
            """));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("DIM a[]=1\nb=SLICE(a,NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("b=SLICE(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=1\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static UwscArray Call(params Value[] arguments) =>
        BuiltinFunctions.Evaluate("SLICE", arguments).AsArray();
}
