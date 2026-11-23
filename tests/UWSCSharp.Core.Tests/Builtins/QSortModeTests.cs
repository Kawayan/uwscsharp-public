using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class QSortModeTests
{
    [Theory]
    [InlineData("QSRT_UNICODEA", "file1,file10,file2", "a,c,b")]
    [InlineData("QSRT_UNICODED", "file2,file10,file1", "b,c,a")]
    [InlineData("QSRT_NATURALA", "file1,file2,file10", "a,b,c")]
    [InlineData("QSRT_NATURALD", "file10,file2,file1", "c,b,a")]
    public async Task ModesPermuteLinkedArrays(string mode,
        string keys, string labels)
    {
        Assert.Equal([keys, labels], await Run($"""
            DIM keys[]="file2","file10","file1"
            DIM labels[]="b","c","a"
            QSORT(keys,{mode},labels)
            PRINT JOIN(keys,",")
            PRINT JOIN(labels,",")
            """));
    }

    [Theory]
    [InlineData(2, "10,2")]
    [InlineData(3, "2,10")]
    [InlineData(4, "2,10")]
    [InlineData(5, "10,2")]
    public async Task NumbersUseStringComparison(int mode, string expected)
    {
        Assert.Equal([expected], await Run($"""
            DIM keys[]=2,10
            QSORT(keys,{mode})
            PRINT JOIN(keys,",")
            """));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task EqualKeysKeepRecoveredSwapAndTrailingEmpty(int mode)
    {
        Assert.Equal(["same,same,", mode is 3 or 5
            ? "x,y,tail" : "y,x,tail"], await Run($"""
            DIM keys[2]="same","same"
            DIM labels[]="x","y","tail"
            QSORT(keys,{mode},labels)
            PRINT JOIN(keys,",")
            PRINT JOIN(labels,",")
            """));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task EmptyAndSingleKeyRemainUnchanged(int mode)
    {
        Assert.Equal(["", "item2", "tag"], await Run($"""
            DIM vacant[0]
            QSORT(vacant,{mode})
            PRINT JOIN(vacant,",")
            DIM keys[]="item2"
            DIM labels[]="tag"
            QSORT(keys,{mode},labels)
            PRINT JOIN(keys,",")
            PRINT JOIN(labels,",")
            """));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task FailedConversionDoesNotWriteBack(int mode)
    {
        var output = new List<string>();
        await Assert.ThrowsAsync<ScriptException>(() => new Interpreter().RunAsync($"""
            DIM keys[]="b",NULL,"a"
            DIM labels[]="b","null","a"
            TRY
                QSORT(keys,{mode},labels)
            FINALLY
                PRINT labels[0]
                PRINT labels[1]
                PRINT labels[2]
            ENDTRY
            """, output.Add));
        Assert.Equal(["b", "null", "a"], output);
    }

    [Theory]
    [InlineData(2, ",項目10,項目2")]
    [InlineData(4, ",項目2,項目10")]
    public async Task InteriorEmptyAndJapaneseKeys(int mode, string expected)
    {
        Assert.Equal([expected], await Run($"""
            DIM keys[2]="項目2",EMPTY,"項目10"
            QSORT(keys,{mode})
            PRINT JOIN(keys,",")
            """));
    }

    [Theory]
    [InlineData(4, "y,x")]
    [InlineData(5, "x,y")]
    public async Task NaturalComparisonIgnoresCaseDespiteSameString(
        int mode, string expected)
    {
        Assert.Equal([expected], await Run($"""
            OPTION SAMESTR
            DIM keys[]="FILE2","file2"
            DIM labels[]="x","y"
            QSORT(keys,{mode},labels)
            PRINT JOIN(labels,",")
            """));
    }
}
