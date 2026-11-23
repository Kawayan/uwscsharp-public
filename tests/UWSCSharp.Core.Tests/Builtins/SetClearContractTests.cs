using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class SetClearContractTests
{
    [Theory]
    [InlineData("SETCLEAR(a)", ",,")]
    [InlineData("SETCLEAR(a,)", ",,")]
    [InlineData("SETCLEAR(a,EMPTY)", ",,")]
    [InlineData("SETCLEAR(a,0)", "0,0,0")]
    [InlineData("SETCLEAR(a,TRUE)", "1,1,1")]
    [InlineData("sEtClEaR(a,FALSE)", "0,0,0")]
    [InlineData("SETCLEAR(a,\"\")", ",,")]
    [InlineData("SETCLEAR(a,\"$10\")", "$10,$10,$10")]
    [InlineData("SETCLEAR(a,\"NaN\")", "NaN,NaN,NaN")]
    [InlineData("SETCLEAR(a,\"😀\")", "😀,😀,😀")]
    public async Task OptionalValueIsCopiedAndOriginalAliasIsIndependent(string call, string expected)
    {
        Assert.Equal([expected, "1,2,3"], await Run(
            "DIM a[]=1,2,3\nb=a\n" + call
            + "\nPRINT JOIN(a,\",\")\nPRINT JOIN(b,\",\")"));
    }

    [Fact]
    public void EveryValueKindPayloadAndNonzeroBoundsArePreserved()
    {
        object instance = new();
        var nested = UwscArray.FromValues([Value.Numeric(7)]);
        var table = UwscHashTable.Create();
        double nan = BitConverter.Int64BitsToDouble(0x7ff8000000000042);
        Value[] values = [Value.Empty, new(ValueKind.Null), Value.Nothing, Value.FromError(5),
            Value.FromObject(instance), Value.FromArray(nested), Value.FromHashTable(table),
            new(ValueKind.Omitted), Value.String("😀\0\ud800"), Value.Numeric(-0d),
            new(ValueKind.Number, nan), new(ValueKind.Number, double.PositiveInfinity),
            new(ValueKind.Number, double.NegativeInfinity)];
        var source = UwscArray.CreateBounded([-2, 5], [-1, 7],
            Enumerable.Range(1, 6).Select(n => Value.Numeric(n)));
        foreach (Value value in values)
        {
            UwscArray filled = ArrayOperations.Fill(source, value);
            Assert.Equal([-2, 5], filled.LowerBounds);
            Assert.Equal([-1, 7], filled.UpperBounds);
            Assert.NotSame(source, filled);
            Assert.All(filled.Values, actual =>
            {
                Assert.Equal(value, actual);
                Assert.Equal(BitConverter.DoubleToInt64Bits(value.Number),
                    BitConverter.DoubleToInt64Bits(actual.Number));
                Assert.Same(value.Array, actual.Array);
                Assert.Same(value.HashTable, actual.HashTable);
                Assert.Same(value.ObjectReference, actual.ObjectReference);
            });
        }

        Assert.Equal(Enumerable.Range(1, 6).Select(n => Value.Numeric(n)), source.Values);
    }

    [Theory]
    [InlineData("SETCLEAR()")]
    [InlineData("SETCLEAR(,Tick())")]
    [InlineData("SETCLEAR(a,Tick(),Tick())")]
    [InlineData("SETCLEAR(Tick(),Tick())")]
    public async Task InvalidCallPreventsArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0", "1,2,3"], await Run(
            "PUBLIC calls=0\nDIM a[]=1,2,3\nTRY\n" + call
            + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\nPRINT JOIN(a,\",\")\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=0\nFEND"));
    }

    [Fact]
    public async Task IndexRunsOnceBeforeValueAndPreservesOtherRowChanges()
    {
        Assert.Equal(["index", "value", "1", "7,7,7,9,5,6"], await Run("""
            PUBLIC a
            PUBLIC calls=0
            DIM initial[1][2]=1,2,3,4,5,6
            a=initial
            SETCLEAR(a[Tick()],FillValue())
            PRINT calls
            PRINT JOIN(a,",")
            FUNCTION Tick()
                PRINT "index"
                calls=calls+1
                RESULT=calls-1
            FEND
            FUNCTION FillValue()
                PRINT "value"
                a[1][0]=9
                RESULT=7
            FEND
            """));
    }

    [Fact]
    public async Task HashKeyIsFrozenAndOtherEntriesSurviveValueEffects()
    {
        Assert.Equal(["1", "x,x", "changed"], await Run("""
            PUBLIC h
            PUBLIC calls=0
            HASHTBL initial
            h=initial
            h[""]=SPLIT("a,b",",")
            h["other"]="old"
            SETCLEAR(h[Key()],FillValue())
            PRINT calls
            PRINT JOIN(h[""],",")
            PRINT h["other"]
            FUNCTION Key()
                calls=calls+1
                RESULT=""
            FEND
            FUNCTION FillValue()
                h["other"]="changed"
                RESULT="x"
            FEND
            """));
    }

    [Fact]
    public async Task WholeShapeReferenceParameterAndEmptyIndexReachTheKernel()
    {
        Assert.Equal(["7,7,7,7", ",,,", "[]"], await Run("""
            DIM a[1][1]=1,2,3,4
            Fill(a)
            PRINT JOIN(a,",")
            SETCLEAR(a[],)
            PRINT JOIN(a,",")
            PRINT "["+SETCLEAR(a,0)+"]"
            PROCEDURE Fill(VAR values)
                SETCLEAR(values,7)
            FEND
            """));
    }

    [Fact]
    public async Task ArrayValueKeepsTheOldArrayWithoutCreatingACycle()
    {
        Assert.Equal(["1,2", "1,2", "1,2"], await Run("""
            DIM a[]=1,2
            b=a
            SETCLEAR(a,a)
            PRINT JOIN(a[0],",")
            PRINT JOIN(a[1],",")
            PRINT JOIN(b,",")
            """));
    }

    [Fact]
    public async Task FailureKeepsTargetAndDoesNotUndoCompletedArgumentEffects()
    {
        Assert.Equal(["1", "1,2"], await Run("""
            PUBLIC calls=0
            DIM a[]=1,2
            TRY
                SETCLEAR(a,Fail())
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
            Run("CONST a=1\nSETCLEAR(a,7)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task EmptyInputStillEvaluatesValueOnceButScalarDoesNot()
    {
        Assert.Equal(["1", "0", "1"], await Run("""
            PUBLIC calls=0
            a=SPLIT("")
            SETCLEAR(a,Tick())
            PRINT calls
            PRINT LENGTH(a)
            a=1
            TRY
                SETCLEAR(a,Tick())
            EXCEPT
                PRINT calls
            ENDTRY
            FUNCTION Tick()
                calls=calls+1
                RESULT=7
            FEND
            """));
    }

    [Fact]
    public async Task IncompatibleParentChangeIsPreservedWhenWriteBackFails()
    {
        Assert.Equal(["caught", "1"], await Run("""
            PUBLIC a
            DIM initial[1][1]=1,2,3,4
            a=initial
            TRY
                SETCLEAR(a[0],ChangeParent())
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT a
            FUNCTION ChangeParent()
                a=1
                RESULT=7
            FEND
            """));
    }

    [Fact]
    public void EmptyAndMaximumArraysKeepTheirShape()
    {
        UwscArray empty = ArrayOperations.Fill(UwscArray.FromValues([]), Value.Numeric(7));
        Assert.Empty(empty.Values);
        Assert.Equal([-1], empty.UpperBounds);
        var source = UwscArray.Create([UwscArray.MaximumElements - 1]);
        UwscArray result = ArrayOperations.Fill(source, Value.String("x"));
        Assert.Equal(source.Count, result.Count);
        Assert.All(result.Values, actual => Assert.Equal("x", actual.Text));
        Assert.Equal(ValueKind.Empty, source.Get(0).Kind);
    }

    [Fact]
    public async Task CancellationPreventsValueEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("SETCLEAR(a,Tick())\nFUNCTION Tick()\n"
                + "PRINT 1\nRESULT=1\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }
}
