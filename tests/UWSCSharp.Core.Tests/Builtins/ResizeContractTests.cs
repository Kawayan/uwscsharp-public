using System.Runtime.CompilerServices;
using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ResizeContractTests
{
    [Theory]
    [InlineData("4", "4", "1,2,3,,")]
    [InlineData("1.9", "1", "1,2")]
    [InlineData("-0.9", "0", "1")]
    [InlineData("-1.9", "-1", "")]
    [InlineData("EMPTY", "0", "1")]
    [InlineData("TRUE", "1", "1,2")]
    [InlineData("FALSE", "0", "1")]
    [InlineData("\" 4e0 \"", "4", "1,2,3,,")]
    public async Task BoundTruncatesAndReplacesOnlyTheTarget(string bound, string upper, string expected)
    {
        Assert.Equal([upper, expected, "1,2,3"], await Run(
            "DIM a[]=1,2,3\nb=a\nPRINT RESIZE(a," + bound
            + ")\nPRINT JOIN(a,\",\")\nPRINT JOIN(b,\",\")"));
    }

    [Theory]
    [InlineData("-2")]
    [InlineData("1000000")]
    [InlineData("1e20")]
    [InlineData("-1e20")]
    [InlineData("NULL")]
    [InlineData("\"$4\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1,000\"")]
    [InlineData("\"\"")]
    public async Task InvalidBoundPreservesTargetAndAssignment(string bound)
    {
        Assert.Equal(["kept", "1,2,3"], await Run(
            "DIM a[]=1,2,3\nr=\"kept\"\nTRY\nr=RESIZE(a," + bound
            + ")\nEXCEPT\nPRINT r\nENDTRY\nPRINT JOIN(a,\",\")"));
    }

    [Theory]
    [InlineData("RESIZE()")]
    [InlineData("RESIZE(,Tick())")]
    [InlineData("RESIZE(a,Tick(),Tick())")]
    [InlineData("RESIZE(Tick(),1)")]
    public async Task InvalidCallPreventsArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run(
            "PUBLIC calls=0\nDIM a[]=1,2,3\nTRY\nPRINT " + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=0\nFEND"));
    }

    [Fact]
    public async Task QueryAcceptsExpressionsAndOmissionWithoutWritingBack()
    {
        Assert.Equal(["2", "2", "1", "1", "-1", "1,2,3"], await Run("""
            DIM a[]=1,2,3
            PRINT RESIZE(a)
            PRINT RESIZE(a,)
            PRINT RESIZE(SPLIT("x,y",","))
            PRINT RESIZE(Make(),)
            PRINT RESIZE(SPLIT(""))
            PRINT JOIN(a,",")
            FUNCTION Make()
                RESULT=SPLIT("x,y",",")
            FEND
            """));
    }

    [Fact]
    public async Task RowIndexRunsOnceAndOtherRowsKeepArgumentEffects()
    {
        Assert.Equal(["index", "bound", "1", "1", "1,2,9,4"], await Run("""
            PUBLIC a
            PUBLIC calls=0
            DIM initial[1][1]=1,2,3,4
            a=initial
            PRINT RESIZE(a[Tick()],Bound())
            PRINT calls
            PRINT JOIN(a,",")
            FUNCTION Tick()
                PRINT "index"
                calls=calls+1
                RESULT=calls-1
            FEND
            FUNCTION Bound()
                PRINT "bound"
                a[1][0]=9
                RESULT=1
            FEND
            """));
    }

    [Fact]
    public async Task PartialShapeMismatchFailsButNestedElementMayChangeShape()
    {
        Assert.Equal(["caught", "1,2,3,4", "3", "1,2,,"], await Run("""
            DIM a[1][1]=1,2,3,4
            TRY
                PRINT RESIZE(a[0],3)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT JOIN(a,",")
            DIM nested[0]
            nested[0]=SPLIT("1,2",",")
            PRINT RESIZE(nested[0],3)
            PRINT JOIN(nested[0],",")
            """));
    }

    [Fact]
    public async Task HashKeyAndVarReferenceWriteBackOnce()
    {
        Assert.Equal(["3", "1", "1,2,,", "3", "1,2,,"], await Run("""
            PUBLIC calls=0
            HASHTBL h
            h[""]=SPLIT("1,2",",")
            PRINT RESIZE(h[Key()],3)
            PRINT calls
            PRINT JOIN(h[""],",")
            DIM a[]=1,2
            Grow(a)
            PRINT RESIZE(a[])
            PRINT JOIN(a,",")
            FUNCTION Key()
                calls=calls+1
                RESULT=""
            FEND
            PROCEDURE Grow(VAR values[])
                RESIZE(values,3)
            FEND
            """));
    }

    [Fact]
    public void RemainingAxesNonzeroBoundsAndRawValuesArePreserved()
    {
        object instance = new();
        double nan = BitConverter.Int64BitsToDouble(0x7ff8000000000042);
        Value[] values = [Value.FromObject(instance), Value.String("😀\0\ud800"),
            new(ValueKind.Number, nan), Value.Numeric(-0d), Value.Nothing, Value.FromError(5)];
        var source = UwscArray.CreateBounded([-2, 5], [-1, 7], values);
        UwscArray grown = source.Resize(0);
        Assert.Equal([-2, 5], grown.LowerBounds);
        Assert.Equal([0, 7], grown.UpperBounds);
        Assert.Equal(values, grown.Values.Take(6));
        Assert.Same(instance, grown.Get(-2, 5).AsObject());
        Assert.Equal(BitConverter.DoubleToInt64Bits(nan),
            BitConverter.DoubleToInt64Bits(grown.Get(-2, 7).Number));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d),
            BitConverter.DoubleToInt64Bits(grown.Get(-1, 5).Number));
        Assert.All(grown.Values.Skip(6), value => Assert.Equal(ValueKind.Empty, value.Kind));
        Assert.Equal(values.Take(3), source.Resize(-2).Values);
        Assert.Empty(source.Resize(-3).Values);
        Assert.Throws<InvalidOperationException>(() => source.Resize(-4));
        Assert.Equal(values, source.Values);
    }

    [Fact]
    public void ExtremeBoundsAndEmptyRemainingAxesRespectLimits()
    {
        var minimum = UwscArray.CreateBounded([int.MinValue], [int.MinValue + 2],
            [Value.Numeric(1), Value.Numeric(2), Value.Numeric(3)]);
        Assert.Single(minimum.Resize(int.MinValue).Values);
        Assert.Throws<InvalidOperationException>(() => minimum.Resize(int.MaxValue));
        var maximum = UwscArray.CreateBounded([int.MaxValue], [int.MaxValue], [Value.Numeric(1)]);
        Assert.Empty(maximum.Resize(int.MaxValue - 1).Values);
        Assert.Throws<InvalidOperationException>(() => maximum.Resize(int.MaxValue - 2));
        var empty = UwscArray.CreateBounded([0, 5], [2, 4]);
        Assert.Empty(empty.Resize(4).Values);
        Assert.Equal([4, 4], empty.Resize(4).UpperBounds);
    }

    [Fact]
    public void MaximumResultIsAllocatedOnceAndExcessIsRejectedBeforeAllocation()
    {
        var source = UwscArray.FromValues([Value.Numeric(7)]);
        source.Resize(3);
        long before = GC.GetAllocatedBytesForCurrentThread();
        UwscArray result = source.Resize(UwscArray.MaximumElements - 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(UwscArray.MaximumElements, result.Count);
        Assert.Equal(Value.Numeric(7), result.Get(0));
        Assert.Equal(ValueKind.Empty, result.Get(UwscArray.MaximumElements - 1).Kind);
        Assert.True(allocated <= (long)Unsafe.SizeOf<Value>() * result.Count + 65536,
            $"確保量: {allocated}");
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidOperationException>(() => source.Resize(UwscArray.MaximumElements));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 65536);
    }

    [Fact]
    public async Task FailureAndCancellationKeepCompletedEffectsSeparateFromWriteBack()
    {
        Assert.Equal(["1", "1,2"], await Run("""
            PUBLIC calls=0
            DIM a[]=1,2
            TRY
                RESIZE(a,Fail())
            EXCEPT
                PRINT calls
            ENDTRY
            PRINT JOIN(a,",")
            FUNCTION Fail()
                calls=calls+1
                RESULT=1/0
            FEND
            """));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("CONST a=1\nRESIZE(a,1)"));
        Assert.Equal(2, error.Line);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT RESIZE(Tick())\nFUNCTION Tick()\n"
                + "PRINT 1\nRESULT=SPLIT(\"\")\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }
}
