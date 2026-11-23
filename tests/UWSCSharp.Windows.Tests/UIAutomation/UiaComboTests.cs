using System.Runtime.Versioning;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class UiaComboTests
{
    private sealed class Pattern : IUiaExpandCollapsePattern
    {
        internal readonly List<string> Calls = [];
        internal Action? OnExpand;
        internal Action? OnCollapse;

        public int State() => 0;

        public void Expand()
        {
            Calls.Add("Expand");
            OnExpand?.Invoke();
        }

        public void Collapse()
        {
            Calls.Add("Collapse");
            OnCollapse?.Invoke();
        }
    }

    [Fact]
    public void ReadsBetweenExpandAndCollapse()
    {
        var pattern = new Pattern();
        Assert.Equal("selected", UiaSelection.ReadExpanded(pattern, () =>
        {
            pattern.Calls.Add("Read");
            return "selected";
        }, default));
        Assert.Equal(["Expand", "Read", "Collapse"], pattern.Calls);
    }

    [Fact]
    public void CancellationBeforeExpandDoesNotMutate()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pattern = new Pattern();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UiaSelection.ReadExpanded(pattern, () => "selected", cancellation.Token));
        Assert.Empty(pattern.Calls);
    }

    [Fact]
    public void CancellationAfterReadStillCollapses()
    {
        using var cancellation = new CancellationTokenSource();
        var pattern = new Pattern();
        Assert.ThrowsAny<OperationCanceledException>(() => UiaSelection.ReadExpanded(pattern, () =>
        {
            cancellation.Cancel();
            return "selected";
        }, cancellation.Token));
        Assert.Equal(["Expand", "Collapse"], pattern.Calls);
    }

    [Fact]
    public void CancellationAfterExpandStillCollapses()
    {
        using var cancellation = new CancellationTokenSource();
        var pattern = new Pattern { OnExpand = cancellation.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UiaSelection.ReadExpanded(pattern, () => throw new InvalidOperationException(), cancellation.Token));
        Assert.Equal(["Expand", "Collapse"], pattern.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsExpandOrReadFailureWhenCollapseAlsoFails(bool failExpand)
    {
        var primary = new InvalidOperationException("取得処理失敗");
        var cleanup = new InvalidOperationException("Collapse失敗");
        var pattern = new Pattern { OnCollapse = () => throw cleanup };
        if (failExpand)
        {
            pattern.OnExpand = () => throw primary;
        }

        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() =>
            UiaSelection.ReadExpanded(pattern, () => throw primary, default)));
        Assert.Same(cleanup, primary.Data["UWSCSharp.UiaComboCollapseError"]);
        Assert.Equal(["Expand", "Collapse"], pattern.Calls);
    }

    [Fact]
    public void CollapseOnlyFailureIsPropagated()
    {
        var failure = new InvalidOperationException("Collapseだけ失敗");
        var pattern = new Pattern { OnCollapse = () => throw failure };
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            UiaSelection.ReadExpanded(pattern, () => "selected", default)));
    }
}
