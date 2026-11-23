using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class ArrayCompletionTests
{
    [Theory]
    [InlineData("DIM a[]=10,20\nPRINT a[\"\"]", "10")]
    [InlineData("DIM a[]=10,20\na[\"\"]=7\nPRINT JOIN(a,\",\")", "7,20")]
    [InlineData("DIM a[\"\"]\nPRINT LENGTH(a)", "1")]
    [InlineData("DIM a[1][\"\"]\nPRINT LENGTH(a[1])", "1")]
    [InlineData("DIM a[]=10,20\nF(a[\"\"])\nPRINT a[0]\nPROCEDURE F(VAR x)\nx=7\nFEND", "7")]
    [InlineData("DIM a[0][0]=10\nF(a[\"\"])\nPRINT a[0,0]\nPROCEDURE F(VAR x[])\nx[\"\"]=7\nFEND", "7")]
    public async Task EmptyTextMeansZeroOnlyInArrayCoordinates(
        string source, string expected)
    {
        Assert.Equal([expected], await Run(source));
    }

    [Theory]
    [InlineData("DIM a[]=10,20\nPRINT a[\" \"]")]
    [InlineData("DIM a[]=10,20\na[\"bad\"]=7")]
    [InlineData("DIM a[\" \"]")]
    [InlineData("DIM a[2147483648]")]
    [InlineData("DIM a[]=10,20\nPRINT a[NULL]")]
    public async Task InvalidCoordinatesRemainScriptErrors(string source)
    {
        await Assert.ThrowsAsync<ScriptException>(() => Run(source));
    }

    [Fact]
    public void EmptyTextIsStillInvalidForGeneralNumericConversion()
    {
        Assert.Throws<InvalidOperationException>(() =>
            NumericOperations.Integer(Value.String("")));
    }

    [Theory]
    [InlineData("F(a,a)", "PROCEDURE F(VAR x[],VAR y[])\nx[0]=7\ny[1]=8\nFEND", "1,8")]
    [InlineData("F(a)", "PROCEDURE F(VAR x[])\nx[0]=7\nx[99]=8\nFEND", "1,2")]
    [InlineData("F(a)", "PROCEDURE F(VAR x[])\nTRY\nx[0]=7\nFINALLY\nx[99]=8\nENDTRY\nFEND", "1,2")]
    [InlineData("F(a)", "PROCEDURE F(VAR x[])\nx[0]=7\nEXIT\nFEND", "7,2")]
    public async Task CopybackUsesNormalReturnAndFormalOrder(
        string call, string routine, string expected)
    {
        Assert.Equal([expected], await Run("DIM a[]=1,2\nTRY\n"
            + call + "\nEXCEPT\nENDTRY\nPRINT JOIN(a,\",\")\n" + routine));
    }

    [Theory]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopybackFailureUpdatesLaterParametersWithoutPublishingResult(
        bool nonnumeric)
    {
        string cursor = nonnumeric ? "\"bad\"" : "9";
        Assert.Equal(["caught", "97|29,31|101|123"], await Run($$"""
            PUBLIC cursor=0
            DIM argument=23,tail=37,assigned=123
            DIM values[1]=29,31
            TRY
                assigned=Change(argument,values[cursor],tail)
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT argument+"|"+values[0]+","+values[1]+"|"+tail+"|"+assigned
            FUNCTION Change(VAR firstValue,VAR secondValue,VAR thirdValue)
                firstValue=97
                secondValue=99
                thirdValue=101
                cursor={{cursor}}
                RESULT=7
            FEND
            """));
    }

    [Fact]
    public async Task CopybackFailureKeepsEarlierWriteAndCallerShape()
    {
        Assert.Equal(["error", "7,2"], await Run("""
            PUBLIC row=0
            DIM a[]=1,2
            TRY
                F(a[0],a[row])
            EXCEPT
                PRINT "error"
            ENDTRY
            PRINT JOIN(a,",")
            PROCEDURE F(VAR x,VAR y)
                x=7
                y=8
                row=99
            FEND
            """));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(3)]
    public void BoundedHelpersUseFlatOffsetsAndPreserveCoordinates(int lower)
    {
        var array = UwscArray.CreateBounded([lower], [lower + 2],
            [Value.Numeric(3), Value.Numeric(1), Value.Numeric(2)]);
        Assert.Equal("1,2", ArrayOperations.Join(array, ",", false, 1, 2));
        UwscArray shifted = ArrayOperations.Shift(array, 1);
        Assert.Equal([lower], shifted.LowerBounds);
        Assert.Equal([lower + 2], shifted.UpperBounds);
        Assert.Equal(ValueKind.Empty, shifted.Get(lower).Kind);
        Assert.Equal(Value.Numeric(3), shifted.Get(lower + 1));
        Assert.Equal(Value.Numeric(3), array.Get(lower));
        UwscArray resized = array.Resize(lower + 3);
        Assert.Equal([lower], resized.LowerBounds);
        Assert.Equal(4, resized.Count);
        Assert.Equal(ValueKind.Empty, resized.Get(lower + 3).Kind);
    }

    [Fact]
    public void NestedSnapshotsAndFailedShapeReplacementKeepOriginalValues()
    {
        var nested = UwscArray.FromValues([Value.Numeric(7)]);
        var array = UwscArray.FromValues([Value.FromArray(nested)]);
        UwscArray changed = array.Set([0], Value.FromArray(
            nested.Set([0], Value.Numeric(9))));
        Assert.Equal(Value.Numeric(7), array.Get(0).AsArray().Get(0));
        Assert.Equal(Value.Numeric(9), changed.Get(0).AsArray().Get(0));
        var matrix = UwscArray.CreateBounded([-1, 5], [0, 6],
            [Value.Numeric(1), Value.Numeric(2), Value.Numeric(3), Value.Numeric(4)]);
        Assert.Throws<InvalidOperationException>(() => matrix.Set([-1],
            Value.FromArray(UwscArray.FromValues([Value.Numeric(8), Value.Numeric(9)]))));
        Assert.Equal(Value.Numeric(1), matrix.Get(-1, 5));
        Assert.Equal(Value.Numeric(4), matrix.Get(0, 6));
    }

    [Fact]
    public void RankAndEmptyAxesHaveExplicitBoundaries()
    {
        Assert.Equal(16, UwscArray.Create(Enumerable.Repeat(0, 16)).Rank);
        Assert.Throws<InvalidOperationException>(() =>
            UwscArray.Create(Enumerable.Repeat(0, 17)));
        var empty = UwscArray.CreateBounded([3, -2], [4, -3]);
        Assert.Equal(0, empty.Count);
        Assert.Equal(0, empty.Get(3).AsArray().Count);
        Assert.Throws<InvalidOperationException>(() => empty.Get(3, -2));
        Assert.Equal(0, empty.Set([3], empty.Get(4)).Count);
    }
}
