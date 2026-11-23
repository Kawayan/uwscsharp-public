using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class QSortContractTests
{
    [Theory]
    [InlineData(0, "3,10,2")]
    [InlineData(1, "2,10,3")]
    public void MixedKeysHaveTheSameOrderForEveryInputPermutation(int mode, string expected)
    {
        Value[] values = [Value.String("10"), Value.String("2"), Value.Numeric(3)];
        foreach (int[] indices in new[] { new[] { 0, 1, 2 }, [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0] })
        {
            var keys = UwscArray.FromValues(indices.Select(index => values[index]));
            UwscArray result = ArrayOperations.Sort([keys], mode)[0];
            Assert.Equal(expected, string.Join(",", result.Values.Select(value => value.Display())));
            Assert.Equal(mode == 0 ? ValueKind.Number : ValueKind.String, result.Get(0).Kind);
            Assert.Equal(indices.Select(index => values[index]), keys.Values);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void InvalidKeysAreRejectedEvenWhenThereIsOnlyOneActiveKey(int mode)
    {
        Value[] invalid = [new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromObject(new object()), Value.FromArray(UwscArray.FromValues([])),
            Value.FromHashTable(UwscHashTable.Create()), new(ValueKind.Omitted),
            new(ValueKind.Number, double.NaN), new(ValueKind.Number, double.PositiveInfinity)];
        foreach (Value value in invalid)
        {
            foreach (Value[] values in new[] { new[] { value }, [Value.String("a"), value, Value.String("b")] })
            {
                var keys = UwscArray.FromValues(values);
                Assert.Throws<InvalidOperationException>(() => ArrayOperations.Sort([keys], mode));
                Assert.Equal(values, keys.Values);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ShortLinkedArraysAreRejectedBeforeAnyResultIsPublished(int mode)
    {
        var keys = UwscArray.FromValues([Value.Numeric(3), Value.Numeric(1), Value.Numeric(2)]);
        var linked = UwscArray.FromValues([Value.String("c"), Value.String("a")]);
        Assert.Throws<InvalidOperationException>(() => ArrayOperations.Sort([keys, linked], mode));
        Assert.Equal([Value.Numeric(3), Value.Numeric(1), Value.Numeric(2)], keys.Values);
        Assert.Equal([Value.String("c"), Value.String("a")], linked.Values);
    }

    [Fact]
    public void TrailingEmptyDoesNotRequireMatchingLinkedTailAndInteriorEmptyIsZero()
    {
        var keys = UwscArray.FromValues([Value.Numeric(2), Value.Empty, Value.Numeric(-1), Value.Numeric(1), Value.Empty]);
        var linked = UwscArray.FromValues([Value.String("two"), Value.String("zero"), Value.String("minus"), Value.String("one")]);
        IReadOnlyList<UwscArray> result = ArrayOperations.Sort([keys, linked]);
        Assert.Equal([Value.Numeric(-1), Value.Empty, Value.Numeric(1), Value.Numeric(2), Value.Empty], result[0].Values);
        Assert.Equal(["minus", "zero", "one", "two"], result[1].Values.Select(value => value.Text));
    }

    [Fact]
    public void NonzeroMultidimensionalBoundsAndArbitraryLinkedPayloadArePreserved()
    {
        object instance = new();
        double nan = BitConverter.Int64BitsToDouble(0x7ff8000000000042);
        var keys = UwscArray.CreateBounded([-2, 5], [-1, 6],
            [Value.Numeric(4), Value.Numeric(1), Value.Numeric(3), Value.Numeric(2)]);
        Value[] payload = [Value.FromObject(instance), new(ValueKind.Number, nan), Value.Nothing, Value.String("😀\0\ud800")];
        var linked = UwscArray.CreateBounded([9, 2], [10, 3], payload);
        IReadOnlyList<UwscArray> result = ArrayOperations.Sort([keys, linked]);
        Assert.Equal([-2, 5], result[0].LowerBounds);
        Assert.Equal([-1, 6], result[0].UpperBounds);
        Assert.Equal([9, 2], result[1].LowerBounds);
        Assert.Equal([10, 3], result[1].UpperBounds);
        Assert.Equal([payload[1], payload[3], payload[2], payload[0]], result[1].Values);
        Assert.Equal(BitConverter.DoubleToInt64Bits(nan), BitConverter.DoubleToInt64Bits(result[1].Values[0].Number));
        Assert.Same(instance, result[1].Values[3].AsObject());
        Assert.Equal(payload, linked.Values);
    }

    [Theory]
    [InlineData("\"0.9\"", "1,2,3")]
    [InlineData("\" 1e0 \"", "3,2,1")]
    [InlineData("EMPTY", "1,2,3")]
    [InlineData("TRUE", "3,2,1")]
    public async Task FiniteModeTruncatesBeforeValidatingTheSixModes(string mode, string expected)
    {
        Assert.Equal([expected], await Run("DIM a[]=3,1,2\nQSORT(a," + mode + ")\nPRINT JOIN(a,\",\")"));
    }

    [Theory]
    [InlineData("6")]
    [InlineData("-1")]
    [InlineData("1e20")]
    [InlineData("\"$1\"")]
    [InlineData("NULL")]
    public async Task InvalidModeDoesNotUpdateInputs(string mode)
    {
        Assert.Equal(["caught", "3,1,2", "c,a,b"], await Run("DIM a[]=3,1,2\nDIM b[]=\"c\",\"a\",\"b\"\nTRY\nQSORT(a,"
            + mode + ",b)\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT JOIN(a,\",\")\nPRINT JOIN(b,\",\")"));
    }

    [Theory]
    [InlineData("QSORT()")]
    [InlineData("QSORT(,Tick())")]
    [InlineData("QSORT(a,Tick(),)")]
    [InlineData("QSORT(a,Tick(),Tick())")]
    [InlineData("QSORT(a,Tick(),a,a,a,a,a,a,a,a,a)")]
    public async Task InvalidCallsPreventArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nDIM a[]=2,1\nTRY\n" + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\nFUNCTION Tick()\ncalls=calls+1\nRESULT=0\nFEND"));
    }

    [Fact]
    public async Task ReferencesRunOnceAndHashValuesWriteBackInArgumentOrder()
    {
        Assert.Equal(["1", "1,2,10,20", "1,2", "a,b"], await Run("""
            PUBLIC calls=0
            DIM rows[1][1]=2,1,20,10
            QSORT(rows[0],,rows[Index()])
            PRINT calls
            PRINT JOIN(rows,",")
            HASHTBL h
            h["keys"]=SPLIT("2,1",",",FALSE,TRUE)
            h["labels"]=SPLIT("b,a",",")
            QSORT(h["keys"],0,h["labels"])
            PRINT JOIN(h["keys"],",")
            PRINT JOIN(h["labels"],",")
            FUNCTION Index()
                calls=calls+1
                RESULT=calls
            FEND
            """));
    }

    [Fact]
    public async Task FailedWritePreparationDoesNotPublishEarlierArrayResults()
    {
        Assert.Equal(["2,1", "7"], await Run("""
            PUBLIC linked
            DIM initial[0][1]=20,10
            linked=initial
            DIM a[]=2,1
            DIM last[0][1]=200,100
            TRY
                QSORT(a,0,linked[0],last[ChangeParent()])
            EXCEPT
            ENDTRY
            PRINT JOIN(a,",")
            PRINT linked
            FUNCTION ChangeParent()
                linked=7
                RESULT=0
            FEND
            """));
    }

    [Fact]
    public void MaximumSizeNineArraysAndCancellationRespectInputOwnership()
    {
        var keys = UwscArray.FromValues(Enumerable.Range(0, UwscArray.MaximumElements)
            .Select(index => Value.Numeric(UwscArray.MaximumElements - index)));
        UwscArray result = ArrayOperations.Sort([keys])[0];
        Assert.Equal(Value.Numeric(1), result.Get(0));
        Assert.Equal(Value.Numeric(UwscArray.MaximumElements), result.Get(UwscArray.MaximumElements - 1));
        Assert.Equal(Value.Numeric(UwscArray.MaximumElements), keys.Get(0));
        var small = UwscArray.FromValues([Value.Numeric(2), Value.Numeric(1)]);
        Assert.Equal(9, ArrayOperations.Sort([.. Enumerable.Repeat(small, 9)]).Count);
        Assert.Throws<InvalidOperationException>(() => ArrayOperations.Sort([.. Enumerable.Repeat(small, 10)]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ArrayOperations.Sort([keys], 0, cancellation.Token));
        Assert.Equal(Value.Numeric(UwscArray.MaximumElements), keys.Get(0));
    }

    [Fact]
    public void CancellationAfterInputReadIsObservedBeforeReturningResults()
    {
        var keys = UwscArray.FromValues([Value.Numeric(2), Value.Numeric(1)]);
        using var cancellation = new CancellationTokenSource();
        var inputs = new CancellingInputs(keys, cancellation);
        Assert.Throws<OperationCanceledException>(() => ArrayOperations.Sort(inputs, 0, cancellation.Token));
        Assert.True(inputs.Read);
        Assert.Equal([Value.Numeric(2), Value.Numeric(1)], keys.Values);
    }

    private sealed class CancellingInputs(UwscArray input, CancellationTokenSource cancellation) : IReadOnlyList<UwscArray>
    {
        public bool Read
        {
            get; private set;
        }

        public int Count => 1;

        public UwscArray this[int index]
        {
            get
            {
                Read = true;
                cancellation.Cancel();
                return input;
            }
        }

        public IEnumerator<UwscArray> GetEnumerator()
        {
            yield return input;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
