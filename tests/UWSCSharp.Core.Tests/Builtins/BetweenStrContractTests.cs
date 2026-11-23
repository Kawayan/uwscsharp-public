using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class BetweenStrContractTests
{
    [Theory]
    [InlineData(0, false, "1")]
    [InlineData(1, false, "1")]
    [InlineData(2, false, "3")]
    [InlineData(2, true, "2")]
    [InlineData(-1, false, "3")]
    [InlineData(-2, false, "1")]
    [InlineData(-2, true, "2")]
    [InlineData(1.9, false, "1")]
    [InlineData(-1.9, false, "3")]
    [InlineData(-0.5, false, "1")]
    public void BothDirectionsAndCountingModesUseTheSameAnchors(
        double occurrence, bool fromStart, string expected)
    {
        Value result = Call("A1A2A3A", "A", "A", occurrence, fromStart);
        Assert.Equal(Value.String(expected), result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void FoundEmptyStringAndMissingEmptyValueRemainDistinct(int direction)
    {
        Assert.Equal(ValueKind.String, Call("AC", "A", "C", direction).Kind);
        Assert.Equal("", Call("AC", "A", "C", direction).Text);
        Assert.Equal(Value.Empty, Call("abc", "missing", "c", direction));
        Assert.Equal(Value.Empty, Call("abc", "a", "missing", direction));
        Assert.Equal(Value.String(""), Call("", "", "", direction));
        Assert.Equal(Value.String("abc"), Call("abc", "", "", direction));
        Assert.Equal(Value.Empty, Call("abc", "", "", direction * 2));
    }

    [Theory]
    [InlineData(1e20)]
    [InlineData(-1e20)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    [InlineData(int.MinValue)]
    public void ExcessiveFiniteCountsReturnEmptyWithoutIntegerOverflow(double occurrence)
    {
        Assert.Equal(Value.Empty, Call("A1C", "A", "C", occurrence));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void SupplementaryCaseComparisonIsConsistentWithoutReversingUnits(int direction)
    {
        Value[] arguments = [Value.String("𐐨body]"), Value.String("𐐀"),
            Value.String("]"), Value.Numeric(direction)];
        Assert.Equal(Value.String("body"), BuiltinFunctions.Evaluate("BETWEENSTR", arguments));
        Assert.Equal(Value.Empty, BuiltinFunctions.Evaluate("BETWEENSTR", arguments, true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void PairBoundariesNulAndIsolatedBodyUnitsArePreserved(int direction)
    {
        Assert.Equal(Value.Empty, Call("A😀B", "\ud83d", "B", direction));
        Assert.Equal(Value.Empty, Call("A😀B", "A", "\ude00", direction));
        Assert.Equal(Value.String("body"), Call("😀body😀", "😀", "😀", direction));
        Assert.Equal(Value.String("\0\ud800\udc00\udfff"),
            Call("A\0\ud800\udc00\udfffB", "A", "B", direction));
        Assert.Equal(Value.String("x"), Call("\ud800x]", "\ud800", "]", direction));
    }

    [Fact]
    public void ReverseSearchSkipsARejectedHalfPairAndContinuesLeftward()
    {
        Assert.Equal(Value.String("first]A😀B"),
            Call("\ud83dfirst]A😀B]", "\ud83d", "]", -1));
        Assert.Equal(Value.String("first"),
            Call("\ud83dfirst]A😀B]", "\ud83d", "]", 1));
        Assert.Equal(Value.Empty, Call("\ud83dfirst]A😀B]", "\ud83d", "]", 2, true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void MarkerTextIsLiteralAndNoInternalSentinelIsInserted(int direction)
    {
        const string sentinel = "_UWSC_DF_BETWEEN_MJ_";
        Assert.Equal(Value.String(sentinel), Call(sentinel, "", "", direction));
        Assert.Equal(Value.String("<#TAB>"), Call("[<#TAB>]", "[", "]", direction));
        Assert.Equal(Value.String(direction == 1 ? "<#TA" : "B>"),
            Call("a<#TAB>b", "a", "b", direction));
    }

    [Theory]
    [InlineData(0.5, "3")]
    [InlineData(-0.5, "3")]
    [InlineData(1.5, "2")]
    [InlineData(-1.5, "2")]
    [InlineData(1e20, "2")]
    public void FiniteFlagsTruncateWithoutAnInt32Limit(double flag, string expected)
    {
        Assert.Equal(Value.String(expected), BuiltinFunctions.Evaluate("BETWEENSTR",
            [Value.String("A1A2A3A"), Value.String("A"), Value.String("A"),
                Value.Numeric(2), Value.Numeric(flag)]));
    }

    [Fact]
    public void ReverseSearchDoesNotAllocateACopyOfTheWholeInput()
    {
        string text = new string('あ', 1_000_001) + "[body]";
        Assert.Equal(Value.String("body"), Call("[body]", "[", "]", -1));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Value result = Call(text, "[", "]", -1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(Value.String("body"), result);
        Assert.InRange(allocated, 0, 65536);
        Assert.Equal(Value.Empty, Call(text, "[", "]", 1e20));
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
            for (int index = 0; index < 5; index++)
            {
                Value[] arguments = [Value.String("A1C"), Value.String("A"),
                    Value.String("C"), Value.Numeric(1), Value.Empty];
                arguments[index] = value;
                Assert.Throws<InvalidOperationException>(() => BuiltinFunctions.Evaluate("BETWEENSTR", arguments));
            }
        }

        Assert.Equal(Value.Numeric(7), array.Get(0));
        Assert.Equal(0, table.Count);
        foreach (string text in new[] { "$1", "NaN", "Infinity", "", "1e", "1\0" })
        {
            for (int index = 3; index < 5; index++)
            {
                Value[] arguments = [Value.String(""), Value.Empty, Value.Empty,
                    Value.Numeric(1), Value.Empty];
                arguments[index] = Value.String(text);
                Assert.Throws<InvalidOperationException>(() => BuiltinFunctions.Evaluate("BETWEENSTR", arguments));
            }
        }
    }

    [Fact]
    public async Task DefaultsFiniteTextAndSpecialCharReachTheSameKernel()
    {
        Assert.Equal(["abc", "abc", "abc", "3", "-0", "2", "2"], await Run("""
            PRINT BETWEENSTR("abc")
            PRINT BETWEENSTR("abc",,,,)
            PRINT BETWEENSTR("abc",EMPTY,EMPTY,EMPTY,EMPTY)
            PRINT BETWEENSTR(123.5,"2",".")
            PRINT BETWEENSTR(-0)
            PRINT BETWEENSTR("A1A2A3A","A","A","2",1e20)
            PRINT bEtWeEnStR("A1A2A3A","A","A",2,1.5)
            """));
        Assert.Equal(["<#TAB>"], await Run(
            "OPTION SPECIALCHAR\nPRINT BETWEENSTR(\"[<#TAB>]\",\"[\",\"]\")"));
    }

    [Theory]
    [InlineData("BETWEENSTR()")]
    [InlineData("BETWEENSTR(,Tick())")]
    [InlineData("BETWEENSTR(Tick(),Tick(),Tick(),Tick(),Tick(),Tick())")]
    public async Task InvalidArityPreventsAllArgumentEffects(string expression)
    {
        Assert.Equal(["caught", "0"], await Run("PUBLIC calls=0\nTRY\nPRINT "
            + expression + "\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT calls\n"
            + "FUNCTION Tick()\ncalls=calls+1\nRESULT=1\nFEND"));
    }

    [Fact]
    public async Task ValidArgumentsRunOnceInOrderAndFailureKeepsTheDestination()
    {
        Assert.Equal(["text", "before", "after", "count", "flag", "1", "kept"], await Run("""
            value=BETWEENSTR(Tick("text","A1C"),Tick("before","A"),Tick("after","C"),Tick("count","1"),Tick("flag","0"))
            PRINT value
            value="kept"
            TRY
                value=BETWEENSTR("abc",NULL)
            EXCEPT
                PRINT value
            ENDTRY
            FUNCTION Tick(name,value)
                PRINT name
                RESULT=value
            FEND
            """));
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            Run("PRINT 1\nPRINT BETWEENSTR(\"abc\",NULL)"));
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public async Task CancellationPreventsArgumentEvaluation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        List<string> output = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new Interpreter().RunAsync("PRINT BETWEENSTR(Tick())\nFUNCTION Tick()\n"
                + "PRINT \"side effect\"\nRESULT=\"a\"\nFEND", output.Add, cancellation.Token));
        Assert.Empty(output);
    }

    private static Value Call(string text, string before, string after,
        double direction, bool fromStart = false) => BuiltinFunctions.Evaluate("BETWEENSTR",
            [Value.String(text), Value.String(before), Value.String(after),
                Value.Numeric(direction), Value.Numeric(fromStart ? 1 : 0)]);
}
