using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ItemCollectorTests
{
    [Fact]
    public void EnumerationIsIndependentAndFreezeRetainsPrefix()
    {
        using var items = new ItemCollector();
        items.Add("first");
        using IEnumerator<string> before = items.GetEnumerator();
        items.Add("second");
        Assert.True(before.MoveNext());
        Assert.Equal("first", before.Current);
        Assert.False(before.MoveNext());
        items.Dispose();
        Assert.Throws<OperationCanceledException>(() => items.Add("late"));
        Assert.Equal(["first", "second"], items);
        Assert.Equal("second", items[1]);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(100, 5)]
    public void LimitFailureRetainsSuccessfulPrefix(int maximumItems, int maximumCharacters)
    {
        using var items = new ItemCollector(maximumItems, maximumCharacters);
        items.Add("first");
        Assert.Throws<InvalidOperationException>(() => items.Add("second"));
        Assert.Equal(["first"], items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningCallerFreezesResultBeforeDelayedWorker(bool cancel)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var first = new ItemCollector();
        // 取消は開始を確認してから通知し、短い期限との競合を避けます。
        TimeSpan? timeout = cancel ? Timeout.InfiniteTimeSpan : null;
        Task<int> pending = NativeControlWorker.RunAsync(_ =>
        {
            first.Add("prefix");
            entered.Set();
            release.Wait();
            try
            {
                Assert.Throws<OperationCanceledException>(() => first.Add("late"));
                return first.Count;
            }
            finally
            {
                finished.Set();
            }
        }, cancellation.Token, timeout);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(() => pending);
            }

            first.Dispose();
            using var next = new ItemCollector();
            next.Add("next");
            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(["prefix"], first);
            Assert.Equal(["next"], next);
        }
        finally
        {
            first.Dispose();
            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        }
    }
}
