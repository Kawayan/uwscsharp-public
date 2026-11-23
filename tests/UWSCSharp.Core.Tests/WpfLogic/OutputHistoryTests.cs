using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class OutputHistoryTests
{
    [Fact]
    public void RetainsNewestOutputEntriesAndDoesNotSplitEmbeddedLines()
    {
        var history = new OutputHistory();
        history.SetMaximumEntries(2);
        history.Append("old");
        history.Append("a\nb");
        history.Append("new");
        Assert.Equal(2, history.Count);
        Assert.Equal("a\nb" + Environment.NewLine + "new" + Environment.NewLine,
            history.Render());
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void EmptyOutputsAreBoundedAndZeroLimitClearsAndDisablesHistory()
    {
        var history = new OutputHistory();
        history.SetMaximumEntries(3);
        for (int index = 0; index < 10; index++)
        {
            history.Append("");
        }

        Assert.Equal(3, history.Count);
        history.SetMaximumEntries(0);
        history.Append("ignored");
        Assert.Empty(history.Render());
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void CharacterBudgetEvictsOldEntriesAndPreservesPairsAtTruncation()
    {
        var history = new OutputHistory();
        string prefix = new('a', OutputText.MaximumLength - Environment.NewLine.Length - 1);
        history.Append("old");
        history.Append(prefix + "😀tail");
        Assert.Equal(1, history.Count);
        Assert.Equal(prefix + Environment.NewLine, history.Render());
        Assert.True(history.Render().Length <= OutputText.MaximumLength);
    }

    [Fact]
    public void GrowthDoesNotRestoreEntriesRemovedBySmallerLimit()
    {
        var history = new OutputHistory();
        history.Append("one");
        history.Append("two");
        history.Append("three");
        history.SetMaximumEntries(1);
        Assert.Equal("three" + Environment.NewLine, history.Render());
        history.SetMaximumEntries(3);
        history.Append("after");
        Assert.Equal(2, history.Count);
        Assert.Equal("three" + Environment.NewLine
            + "after" + Environment.NewLine, history.Render());
    }

    [Fact]
    public void UpperEntryLimitAlsoRespectsCharacterBudget()
    {
        var history = new OutputHistory();
        history.SetMaximumEntries(100000);
        for (int index = 0; index <= 100000; index++)
        {
            history.Append("");
        }

        Assert.Equal(OutputText.MaximumLength / Environment.NewLine.Length,
            history.Count);
        Assert.Equal(OutputText.MaximumLength, history.Render().Length);
    }

    [Fact]
    public void ResetRestoresDefaultLimitWithoutReusingPriorEntries()
    {
        var history = new OutputHistory();
        history.SetMaximumEntries(1);
        history.Append("old");
        history.Reset();
        history.Append("new1");
        history.Append("new2");
        Assert.Equal(2, history.Count);
        Assert.Equal("new1" + Environment.NewLine + "new2" + Environment.NewLine,
            history.Render());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100001)]
    public void InvalidLimitLeavesPriorHistory(int limit)
    {
        var history = new OutputHistory();
        history.Append("old");
        Assert.Throws<ArgumentOutOfRangeException>(() => history.SetMaximumEntries(limit));
        Assert.Equal("old" + Environment.NewLine, history.Render());
    }
}
