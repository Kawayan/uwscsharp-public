using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AccessibleTextInputTests
{
    [Theory]
    [InlineData(1, false, "enabled")]
    [InlineData(-1, true, "disabled")]
    [InlineData(0, false, "focused")]
    public void WriterSharesBoundedSelectionAndKeepsOtherValues(
        int occurrence, bool includeDisabled, string expected)
    {
        var disabled = new AccessibleTestProvider(42, 1, "", "disabled");
        var enabled = new AccessibleTestProvider(42, 0, "", "enabled");
        var focused = new AccessibleTestProvider(42, 4, "", "focused");
        var root = new AccessibleTestProvider(10, 0, "", "",
            disabled, enabled, focused);
        List<string> result = AccessibleItems.ReadProvider(root, 0, false,
            includeDisabled, 1, default, 3, occurrence,
            write: (target, child, current) =>
            {
                Assert.Equal(expected, current);
                target.SetValue(child, current + "追記");
            });
        Assert.Equal([expected], result);
        Assert.Equal(expected == "disabled" ? "disabled追記" : "disabled",
            ((IAccessibleNative)disabled).Value(0));
        Assert.Equal(expected == "enabled" ? "enabled追記" : "enabled",
            ((IAccessibleNative)enabled).Value(0));
        Assert.Equal(expected == "focused" ? "focused追記" : "focused",
            ((IAccessibleNative)focused).Value(0));
    }

    [Fact]
    public void CellWriterPreservesSparseSimpleChildIdentity()
    {
        var first = new AccessibleTestProvider(29, 0, "", "first");
        var second = new AccessibleTestProvider(29, 0, "", "second");
        var root = new AccessibleSimpleProvider([7, 103], [first, second]);
        _ = AccessibleItems.ReadProvider(root, 0, false, true, 1,
            default, 5, 2, write: (target, child, _) =>
            {
                Assert.Equal(103, child);
                target.SetValue(child, "更新");
            });
        Assert.Equal("first", ((IAccessibleNative)first).Value(0));
        Assert.Equal("更新", ((IAccessibleNative)second).Value(0));
    }

    [Fact]
    public void CancellationBeforeSelectedWriteLeavesValueUntouched()
    {
        using var stop = new CancellationTokenSource();
        var target = new AccessibleTestProvider(42, 0, "", "before")
        {
            ReadingValue = stop.Cancel
        };
        Assert.Throws<OperationCanceledException>(() =>
            AccessibleItems.ReadProvider(target, 0, false, true, 1,
                stop.Token, 3, 1, write: (selected, child, _) =>
                    selected.SetValue(child, "after")));
        target.ReadingValue = null;
        Assert.Equal("before", ((IAccessibleNative)target).Value(0));
    }
}
