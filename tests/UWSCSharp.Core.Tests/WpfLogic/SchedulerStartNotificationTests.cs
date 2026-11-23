using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerStartNotificationTests
{
    [Fact]
    public void DisabledNotificationNeverCallsBackend()
    {
        var notification = new SchedulerStartNotification(false, () => throw new Exception());
        Assert.False(notification.NotifyStarted());
        Assert.False(notification.NotifyStarted());
        Assert.Null(notification.Error);
    }

    [Fact]
    public void RepeatedStartOnlyNotifiesOnce()
    {
        int calls = 0;
        var notification = new SchedulerStartNotification(true, () => calls++);
        Assert.True(notification.NotifyStarted());
        Assert.False(notification.NotifyStarted());
        Assert.Equal(1, calls);
        Assert.Null(notification.Error);
    }

    [Fact]
    public void FailureIsRecordedWithoutThrowingOrRetrying()
    {
        int calls = 0;
        var error = new IOException("通知失敗");
        var notification = new SchedulerStartNotification(true, () =>
        {
            calls++;
            throw error;
        });
        Assert.True(notification.NotifyStarted());
        Assert.False(notification.NotifyStarted());
        Assert.Same(error, notification.Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ConcurrentStartsDoNotDuplicateNotification()
    {
        int calls = 0;
        var notification = new SchedulerStartNotification(true, () => Interlocked.Increment(ref calls));
        Parallel.For(0, 64, _ => notification.NotifyStarted());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SeparateExecutionsDoNotShareDeduplication()
    {
        int calls = 0;
        var first = new SchedulerStartNotification(true, () => calls++);
        var second = new SchedulerStartNotification(true, () => calls++);
        Assert.True(first.NotifyStarted());
        Assert.True(second.NotifyStarted());
        Assert.Equal(2, calls);
    }
}
