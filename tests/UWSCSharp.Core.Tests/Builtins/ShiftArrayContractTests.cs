using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ShiftArrayContractTests
{
    [Theory]
    [InlineData("0", "1,2,3")]
    [InlineData("1.9", ",1,2")]
    [InlineData("-1.9", "2,3,")]
    [InlineData("1e20", ",,")]
    [InlineData("-1e20", ",,")]
    [InlineData("2147483647", ",,")]
    [InlineData("-2147483648", ",,")]
    [InlineData("EMPTY", "1,2,3")]
    [InlineData("TRUE", ",1,2")]
    [InlineData("FALSE", "1,2,3")]
    [InlineData("\" 1e0 \"", ",1,2")]
    public async Task FiniteShiftTruncatesAndPreservesCount(string shift, string expected)
    {
        Assert.Equal([expected, "1,2,3"], await Run(
            "DIM a[]=1,2,3\nb=a\nSHIFTARRAY(a," + shift
            + ")\nPRINT JOIN(a,\",\")\nPRINT JOIN(b,\",\")"));
    }

    [Theory]
    [InlineData("\"$10\"")]
    [InlineData("\"NaN\"")]
    [InlineData("\"1e309\"")]
    [InlineData("\"1,000\"")]
    [InlineData("\"\"")]
    [InlineData("NULL")]
    [InlineData("SPLIT(\"x\")")]
    public async Task InvalidShiftDoesNotWriteBack(string shift)
    {
        Assert.Equal(["caught", "1,2,3"], await Run(
            "DIM a[]=1,2,3\nTRY\nSHIFTARRAY(a," + shift
            + ")\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT JOIN(a,\",\")"));
    }

    [Theory]
    [InlineData("SHIFTARRAY()")]
    [InlineData("SHIFTARRAY(a,)")]
    [InlineData("SHIFTARRAY(a,Tick(),Tick())")]
    [InlineData("SHIFTARRAY(Tick(),1)")]
    public async Task InvalidCallPreventsArgumentEffects(string call)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nDIM a[]=1\nTRY\n"
            + call + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task RowIndexRunsOnceBeforeShiftAndKeepsOtherRowChanges()
    {
        Assert.Equal(["index", "shift", "1", ",1,2,9,5,6"], await Run("""
            PUBLIC calls=0
            PUBLIC a
            DIM initial[1][2]=1,2,3,4,5,6
            a=initial
            SHIFTARRAY(a[Tick()],Move())
            PRINT calls
            PRINT JOIN(a,",")
            FUNCTION Tick()
                PRINT "index"
                calls=calls+1
                RESULT=0
            FEND
            FUNCTION Move()
                PRINT "shift"
                a[1][0]=9
                RESULT=1
            FEND
            """));
    }

    [Fact]
    public async Task HashKeyRunsOnceAndWritesOnlyTheSelectedEntry()
    {
        Assert.Equal(["1", ",a", "other"], await Run("""
            PUBLIC calls=0
            HASHTBL h
            h["key"]=SPLIT("a,b",",")
            h["other"]="other"
            SHIFTARRAY(h[Tick()],1)
            PRINT calls
            PRINT JOIN(h["key"],",")
            PRINT h["other"]
            FUNCTION Tick()
                calls=calls+1
                RESULT="key"
            FEND
            """));
    }

    [Fact]
    public async Task ReferenceParameterAndEmptyIndexUpdateTheirTargets()
    {
        Assert.Equal([",,1"], await Run("""
            DIM a[]=1,2,3
            Move(a)
            SHIFTARRAY(a[],1)
            PRINT JOIN(a,",")
            PROCEDURE Move(VAR values)
                SHIFTARRAY(values,1)
            FEND
            """));
    }

    [Fact]
    public async Task InvalidSourcePreventsShiftEffectsAndConstantIsRejected()
    {
        Assert.Equal(["caught", "0"], await Run("""
            PUBLIC calls=0
            a=1
            TRY
                SHIFTARRAY(a,Tick())
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT calls
            FUNCTION Tick()
                calls=calls+1
                RESULT=1
            FEND
            """));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("CONST a=1\nSHIFTARRAY(a,1)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void ShapeBoundsAndRawValuesArePreservedWithoutConversion()
    {
        object instance = new();
        Value[] values = [Value.FromObject(instance), Value.String("😀\0\ud800"),
            new(ValueKind.Number, double.NaN), Value.Numeric(-0d), Value.Nothing, Value.FromError(5)];
        var source = UwscArray.CreateBounded([-2, 5], [-1, 7], values);
        UwscArray shifted = ArrayOperations.Shift(source, 1);
        Assert.Equal([-2, 5], shifted.LowerBounds);
        Assert.Equal([-1, 7], shifted.UpperBounds);
        Assert.Equal(Value.Empty, shifted.Values[0]);
        Assert.Equal(values.Take(5), shifted.Values.Skip(1));
        Assert.Same(instance, shifted.Values[1].AsObject());
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d),
            BitConverter.DoubleToInt64Bits(shifted.Values[4].Number));
        Assert.Equal(values, source.Values);
    }

    [Fact]
    public void EmptyAndMaximumInputsRespectTheExistingLimit()
    {
        Assert.Empty(ArrayOperations.Shift(UwscArray.FromValues([]), int.MinValue).Values);
        var source = UwscArray.Create([UwscArray.MaximumElements - 1]);
        UwscArray shifted = ArrayOperations.Shift(source, int.MaxValue);
        Assert.Equal(source.Count, shifted.Count);
        Assert.All(shifted.Values, value => Assert.Equal(ValueKind.Empty, value.Kind));
    }

    [Fact]
    public async Task ErrorsCarryLinesAndCancellationPreventsEffects()
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("DIM a[]=1\nSHIFTARRAY(a,NULL)"));
        Assert.Equal(2, error.Line);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("SHIFTARRAY(a,Tick())\nFUNCTION Tick()\n"
                + "PRINT 1\nRESULT=1\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }
}
