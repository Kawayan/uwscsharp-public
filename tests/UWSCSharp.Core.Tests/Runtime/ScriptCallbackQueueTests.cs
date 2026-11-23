using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptCallbackQueueTests
{
    [Fact]
    public async Task NotificationsWaitForCurrentCallbackWithoutReentry()
    {
        var queue = new ScriptCallbackQueue<int>(4);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> events = [];
        ScriptCallbackQueue<int>.Registration registration = queue.Register(async (value, token) =>
        {
            events.Add(value);
            Assert.False(await queue.DispatchOneAsync(token));
            if (value == 1)
            {
                entered.SetResult();
                await release.Task;
            }

            events.Add(-value);
        });
        queue.Publish(registration, 1);
        Task<bool> first = queue.DispatchOneAsync(default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Publish(registration, 2);
        queue.Publish(registration, 3);
        Assert.False(await queue.DispatchOneAsync(default));
        Assert.Equal([1], events);
        release.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await queue.DispatchOneAsync(default));
        Assert.True(await queue.DispatchOneAsync(default));
        Assert.Equal([1, -1, 2, -2, 3, -3], events);
    }

    [Fact]
    public async Task RemovalDropsOldNotificationsAndPreservesOtherOrder()
    {
        var queue = new ScriptCallbackQueue<int>(3);
        List<int> events = [];
        ValueTask Record(int value, CancellationToken token)
        {
            events.Add(value);
            return ValueTask.CompletedTask;
        }

        ScriptCallbackQueue<int>.Registration old = queue.Register(Record);
        ScriptCallbackQueue<int>.Registration other = queue.Register(Record);
        queue.Publish(other, 1);
        queue.Publish(old, 9);
        queue.Publish(other, 2);
        queue.Unregister(old);
        ScriptCallbackQueue<int>.Registration replacement = queue.Register(Record);
        Assert.Equal(ScriptCallbackQueue<int>.PublishResult.Inactive, queue.Publish(old, 8));
        Assert.Equal(ScriptCallbackQueue<int>.PublishResult.Accepted, queue.Publish(replacement, 3));
        while (await queue.DispatchOneAsync(default))
        {
        }

        Assert.Equal([1, 2, 3], events);
        Assert.False(queue.Ready.IsCompleted);
    }

    [Fact]
    public async Task CloseDuringCallbackDropsPendingAndRejectsFutureNotifications()
    {
        var queue = new ScriptCallbackQueue<int>(2);
        ScriptCallbackQueue<int>.Registration registration = queue.Register((value, token) =>
        {
            queue.Close();
            return ValueTask.CompletedTask;
        });
        queue.Publish(registration, 1);
        queue.Publish(registration, 2);
        Assert.True(await queue.DispatchOneAsync(default));
        Assert.False(await queue.DispatchOneAsync(default));
        Assert.True(queue.Ready.IsCompleted);
        Assert.Equal(ScriptCallbackQueue<int>.PublishResult.Inactive, queue.Publish(registration, 3));
        Assert.Throws<ObjectDisposedException>(() => queue.Register((value, token) => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task CapacityAndCancellationDoNotDiscardAcceptedNotifications()
    {
        var queue = new ScriptCallbackQueue<int>(1);
        int observed = 0;
        ScriptCallbackQueue<int>.Registration registration = queue.Register((value, token) =>
        {
            observed = value;
            return ValueTask.CompletedTask;
        });
        Task ready = queue.Ready;
        Assert.Equal(ScriptCallbackQueue<int>.PublishResult.Accepted, queue.Publish(registration, 7));
        await ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScriptCallbackQueue<int>.PublishResult.Full, queue.Publish(registration, 8));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            queue.DispatchOneAsync(new CancellationToken(true)).AsTask());
        Assert.True(await queue.DispatchOneAsync(default));
        Assert.Equal(7, observed);
    }

    [Fact]
    public async Task CallbackFailureReleasesDispatchGuard()
    {
        var queue = new ScriptCallbackQueue<int>(2);
        var failure = new InvalidOperationException("callback");
        ScriptCallbackQueue<int>.Registration registration = queue.Register((value, token) =>
            value == 1 ? ValueTask.FromException(failure) : ValueTask.CompletedTask);
        queue.Publish(registration, 1);
        queue.Publish(registration, 2);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.DispatchOneAsync(default).AsTask()));
        Assert.True(await queue.DispatchOneAsync(default));
    }
}
