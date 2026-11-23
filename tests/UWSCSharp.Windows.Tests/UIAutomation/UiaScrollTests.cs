using System.Runtime.Versioning;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class UiaScrollTests
{
    private sealed class FakeScroll : IUiaScrollPattern
    {
        internal double Saved = 42;
        internal readonly List<(double Horizontal, double Vertical)> Calls = [];
        internal Action<double>? Set;
        internal Action? Read;

        public void Scroll(int horizontalAmount, int verticalAmount) => throw new NotSupportedException();

        public void SetScrollPercent(double horizontalPercent, double verticalPercent)
        {
            Calls.Add((horizontalPercent, verticalPercent));
            Set?.Invoke(verticalPercent);
        }

        public double HorizontalScrollPercent() => throw new NotSupportedException();

        public double HorizontalViewSize() => 100;

        public double VerticalViewSize() => 50;

        public bool HorizontallyScrollable() => false;

        public bool VerticallyScrollable() => true;

        public double VerticalScrollPercent()
        {
            Read?.Invoke();
            return Saved;
        }
    }

    [Fact]
    public void RestoresBeforeReturningAndSkipsNonScrollableAxis()
    {
        var scroll = new FakeScroll();
        UiaSelection.PrepareScroll(scroll, default);
        Assert.Equal([(-1d, 100d), (-1d, 42d)], scroll.Calls);
        scroll.Calls.Clear();
        scroll.Saved = -1;
        UiaSelection.PrepareScroll(scroll, default);
        Assert.Empty(scroll.Calls);
    }

    [Fact]
    public void CancellationBeforeSettingMakesNoMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var scroll = new FakeScroll { Read = cancellation.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UiaSelection.PrepareScroll(scroll, cancellation.Token));
        Assert.Empty(scroll.Calls);
    }

    [Fact]
    public void RestoreOnlyFailureIsPropagated()
    {
        var failure = new InvalidOperationException("復元だけ失敗");
        var scroll = new FakeScroll
        {
            Set = value =>
            {
                if (value != 100)
                {
                    throw failure;
                }
            }
        };
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            UiaSelection.PrepareScroll(scroll, default)));
    }

    [Theory]
    [InlineData(-2d)]
    [InlineData(101d)]
    [InlineData(double.NaN)]
    public void InvalidSavedPositionIsRejectedBeforeMutation(double position)
    {
        var scroll = new FakeScroll { Saved = position };
        Assert.Throws<InvalidOperationException>(() => UiaSelection.PrepareScroll(scroll, default));
        Assert.Empty(scroll.Calls);
    }

    [Fact]
    public void CancellationAfterSettingStillRestores()
    {
        using var cancellation = new CancellationTokenSource();
        var scroll = new FakeScroll { Set = value => { if (value == 100) { cancellation.Cancel(); } } };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            UiaSelection.PrepareScroll(scroll, cancellation.Token));
        Assert.Equal([(-1d, 100d), (-1d, 42d)], scroll.Calls);
    }

    [Fact]
    public void SetAndRestoreFailuresKeepOriginalException()
    {
        var primary = new InvalidOperationException("設定途中で失敗");
        var cleanup = new InvalidOperationException("復元失敗");
        var scroll = new FakeScroll { Set = value => throw (value == 100 ? primary : cleanup) };
        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() =>
            UiaSelection.PrepareScroll(scroll, default)));
        Assert.Same(cleanup, primary.Data["UWSCSharp.UiaScrollRestoreError"]);
        Assert.Equal(2, scroll.Calls.Count);
    }
}
