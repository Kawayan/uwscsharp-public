using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class MultipleListMatcherTests
{
    [Fact]
    public void WidthMappingPreservesLongAsciiSuffixAcrossRepeatedCalls()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string prefix = new('a', 2048);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            Assert.Equal(prefix + "b", NativeListNameMatcher.HalfWidth(prefix + "b"));
            string candidate = NativeListNameMatcher.AsciiUpper(prefix);
            string desired = NativeListNameMatcher.AsciiUpper(prefix + "b");
            string mappedCandidate = NativeListNameMatcher.HalfWidth(candidate);
            string mappedDesired = NativeListNameMatcher.HalfWidth(desired);
            Assert.False(NativeListNameMatcher.Matches(prefix, prefix + "b", false),
                $"mapped {mappedCandidate.Length}:{mappedCandidate[^1]} / {mappedDesired.Length}:{mappedDesired[^1]}");
            Assert.False(AccessibleItems.MatchesName(prefix, prefix + "b"));
            Assert.True(AccessibleItems.MatchesName(prefix + "Ａ", prefix + "A"));
        }
    }

    [Theory]
    [InlineData("", "anything", true)]
    [InlineData(" ", "anything", false)]
    [InlineData(" ", "has space", true)]
    [InlineData(" ALPHA ", "alpha", false)]
    [InlineData("pha", "ALPHA", true)]
    [InlineData("Ａ", "before A after", true)]
    public void SingleShortUsesPartialMatchWithoutTrimming(string name, string item, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, NativeListNameMatcher.Matches(item, name, true));
    }

    [Theory]
    [InlineData("\t\t", 0)]
    [InlineData(" \t\t ", 2)]
    [InlineData("a\ta", 2)]
    public void MultipleShortKeepsWhitespaceTerms(string names, int count)
    {
        Assert.Equal(count, new MultipleListMatcher(names, true).Count);
    }

    [Theory]
    [InlineData("\t \tfirst\t\tsecond\t", 2)]
    [InlineData("\t\t", 0)]
    [InlineData("dup\tdup", 2)]
    [InlineData("\"first\tsecond\"", 2)]
    public void SplitsTabsSkipsEmptyAndPreservesDuplicateTerms(string names, int count)
    {
        Assert.Equal(count, new MultipleListMatcher(names).Count);
    }

    [Theory]
    [InlineData("alpha", " ALPHA ", true)]
    [InlineData("é", "É", false)]
    [InlineData("A", "Ａ", true)]
    [InlineData("A", "ａ", false)]
    [InlineData("A", "　Ａ　", false)]
    [InlineData("B\tA", "　Ａ　", true)]
    [InlineData("ガ", "ｶﾞ", true)]
    public void UsesAsciiUpperTrimAndStatefulHalfWidth(string names, string item, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, new MultipleListMatcher(names).Matches(item));
        if (!names.Contains('\t'))
        {
            Assert.Equal(expected, NativeListNameMatcher.Matches(item, names, false));
        }
    }
}
