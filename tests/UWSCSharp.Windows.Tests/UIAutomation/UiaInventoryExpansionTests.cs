using System.Runtime.Versioning;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class UiaInventoryExpansionTests
{
    private sealed class Pattern(int initial) : IUiaExpandCollapsePattern
    {
        internal readonly List<string> Calls = [];
        internal Action? OnReadState;
        internal Action? OnCollapse;
        internal int Current = initial;

        public int State()
        {
            Calls.Add("state");
            OnReadState?.Invoke();
            return Current;
        }

        public void Expand()
        {
            Calls.Add("expand");
            Current = 1;
        }

        public void Collapse()
        {
            Calls.Add("collapse");
            OnCollapse?.Invoke();
            Current = 0;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ReadingPreservesInitialState(int initial)
    {
        var pattern = new Pattern(initial);
        UiaItemInventory.ReadExpandedPreservingState(pattern,
            () => pattern.Calls.Add("read"), default);
        Assert.Equal(initial, pattern.Current);
        Assert.Equal(initial == 0 ? new[] { "state", "expand", "read", "collapse" }
            : ["state", "read"], pattern.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(4)]
    public void UnsupportedStateDoesNotMutateOrRead(int state)
    {
        var pattern = new Pattern(state);
        bool read = false;
        Exception? failure = Record.Exception(() =>
            UiaItemInventory.ReadExpandedPreservingState(
                pattern, () => read = true, default));
        if (state == 2)
        {
            Assert.IsType<NotSupportedException>(failure);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(failure);
        }

        Assert.False(read);
        Assert.Equal(["state"], pattern.Calls);
        Assert.Equal(state, pattern.Current);
    }

    [Fact]
    public void CancellationWhileReadingStatePreventsExpansion()
    {
        using var cancellation = new CancellationTokenSource();
        var pattern = new Pattern(0) { OnReadState = cancellation.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UiaItemInventory.ReadExpandedPreservingState(pattern,
                () => throw new Xunit.Sdk.XunitException("読取りは禁止です。"), cancellation.Token));
        Assert.Equal(["state"], pattern.Calls);
    }

    [Fact]
    public void ReadFailureKeepsPrimaryWhenRestorationAlsoFails()
    {
        var primary = new InvalidOperationException("読取り失敗");
        var restoration = new InvalidOperationException("復元失敗");
        var pattern = new Pattern(0) { OnCollapse = () => throw restoration };
        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() =>
            UiaItemInventory.ReadExpandedPreservingState(pattern, () => throw primary, default)));
        Assert.Same(restoration, primary.Data["UWSCSharp.UiaItemExpansionRestoreError"]);
        Assert.Equal(["state", "expand", "collapse"], pattern.Calls);
    }

    [Fact]
    public void CancellationAfterReadRestoresCollapsedState()
    {
        using var cancellation = new CancellationTokenSource();
        var pattern = new Pattern(0);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UiaItemInventory.ReadExpandedPreservingState(pattern, cancellation.Cancel, cancellation.Token));
        Assert.Equal(0, pattern.Current);
        Assert.Equal(["state", "expand", "collapse"], pattern.Calls);
    }
}
