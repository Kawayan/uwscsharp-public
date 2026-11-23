using System.Collections.Concurrent;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// Taskと偽送信だけを使い、音声API・COM・画面・入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechDispatchQueueTests
{
    [Fact]
    public async Task SendsInOrderAndPreservesTextAndFlags()
    {
        var requests = new ConcurrentQueue<SpeechDispatchRequest>();
        TaskCompletionSource started = Signal();
        TaskCompletionSource release = Signal();
        await using var queue = new SpeechDispatchQueue(async (request, token) =>
        {
            requests.Enqueue(request);
            if (request.Text == "最初")
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
        });
        Task first = queue.Enqueue(new("最初", 1));
        await Bounded(started.Task);
        Task second = queue.Enqueue(new("次\0の文字", 3));
        Assert.Single(requests);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Bounded(Task.WhenAll(first, second));
        Assert.Equal([new("最初", 1), new SpeechDispatchRequest("次\0の文字", 3)],
            [.. requests]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearsPendingWithoutCancellingActiveSend(bool replace)
    {
        TaskCompletionSource started = Signal();
        TaskCompletionSource release = Signal();
        var sent = new ConcurrentQueue<string>();
        CancellationToken activeToken = default;
        await using var queue = new SpeechDispatchQueue(async (request, token) =>
        {
            sent.Enqueue(request.Text);
            if (request.Text == "active")
            {
                activeToken = token;
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
        });
        Task active = queue.Enqueue(new("active", 1));
        await Bounded(started.Task);
        Task skipped = queue.Enqueue(new("skip", 1));
        if (!replace)
        {
            queue.DiscardPending();
        }

        Task next = queue.Enqueue(new("next", 3), discardPending: replace);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => skipped);
        Assert.False(activeToken.IsCancellationRequested);
        Assert.False(active.IsCompleted);
        release.SetResult();
        await Bounded(Task.WhenAll(active, next));
        Assert.Equal(["active", "next"], [.. sent]);
    }

    [Fact]
    public async Task DisposalCancelsPendingAndWaitsForActiveCleanup()
    {
        TaskCompletionSource started = Signal();
        TaskCompletionSource cancelling = Signal();
        TaskCompletionSource finish = Signal();
        var queue = new SpeechDispatchQueue(async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                cancelling.SetResult();
                await finish.Task;
            }
        });
        Task active = queue.Enqueue(new("active", 1));
        await Bounded(started.Task);
        Task skipped = queue.Enqueue(new("pending", 1));
        Task disposing = queue.DisposeAsync().AsTask();
        try
        {
            await Bounded(cancelling.Task);
            Assert.Same(disposing, queue.DisposeAsync().AsTask());
            Assert.False(disposing.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => { _ = queue.Enqueue(new("late", 1)); });
            Assert.Throws<ObjectDisposedException>(queue.DiscardPending);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => skipped);
        }
        finally
        {
            finish.TrySetResult();
            await Bounded(disposing);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
    }

    [Fact]
    public async Task SendFailureAndCancellationDoNotPoisonLaterRequests()
    {
        var failure = new InvalidOperationException("send failed");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var queue = new SpeechDispatchQueue((request, _) => request.Text switch
        {
            "fail" => throw failure,
            "cancel" => Task.FromCanceled(cancelled.Token),
            _ => Task.CompletedTask,
        });
        Task failed = queue.Enqueue(new("fail", 1));
        Task skipped = queue.Enqueue(new("cancel", 1));
        Task sent = queue.Enqueue(new("success", 1));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => failed));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => skipped);
        await Bounded(sent);
    }

    [Fact]
    public async Task SenderCanReenterQueueWithoutHoldingOwnerLock()
    {
        SpeechDispatchQueue? queue = null;
        Task? nested = null;
        queue = new SpeechDispatchQueue((request, _) =>
        {
            if (request.Text == "outer")
            {
                queue!.DiscardPending();
                nested = queue.Enqueue(new("inner", 1));
            }

            return Task.CompletedTask;
        });
        await using (queue)
        {
            await Bounded(queue.Enqueue(new("outer", 1)));
            await Bounded(Assert.IsAssignableFrom<Task>(nested));
        }
    }

    [Fact]
    public async Task SenderCanInitiateDisposalWithoutWaitingForItself()
    {
        SpeechDispatchQueue? queue = null;
        Task? disposing = null;
        queue = new SpeechDispatchQueue((_, _) =>
        {
            disposing = queue!.DisposeAsync().AsTask();
            return Task.CompletedTask;
        });
        await Bounded(queue.Enqueue(new("finish", 1)));
        await Bounded(Assert.IsAssignableFrom<Task>(disposing));
    }

    [Fact]
    public async Task ConcurrentProducersNeverOverlapSendCalls()
    {
        var requests = new ConcurrentQueue<int>();
        int active = 0;
        await using var queue = new SpeechDispatchQueue(async (request, _) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            await Task.Yield();
            requests.Enqueue(request.Flags);
            Assert.Equal(0, Interlocked.Decrement(ref active));
        });
        Task[] producers = [.. Enumerable.Range(0, 100).Select(index =>
            Task.Run(() => queue.Enqueue(new("text", index))))];
        await Bounded(Task.WhenAll(producers));
        Assert.Equal(Enumerable.Range(0, 100), requests.Order());
    }

    [Fact]
    public async Task InvalidReplacementDoesNotDiscardValidPendingRequest()
    {
        TaskCompletionSource started = Signal();
        TaskCompletionSource release = Signal();
        await using var queue = new SpeechDispatchQueue(async (request, token) =>
        {
            if (request.Text == "first")
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
            }
        });
        Task first = queue.Enqueue(new("first", 1));
        await Bounded(started.Task);
        Task next = queue.Enqueue(new("valid", int.MinValue));
        Assert.Throws<ArgumentNullException>(() => { _ = queue.Enqueue(null!, true); });
        Assert.Throws<ArgumentNullException>(() => { _ = queue.Enqueue(new(null!, 1), true); });
        Assert.False(next.IsCompleted);
        release.SetResult();
        await Bounded(Task.WhenAll(first, next));
    }

    [Fact]
    public async Task CancellationCallbackFailureStillWaitsForSender()
    {
        TaskCompletionSource started = Signal();
        TaskCompletionSource cancelled = Signal();
        var queue = new SpeechDispatchQueue(async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                cancelled.SetResult();
                throw new InvalidOperationException("callback failed");
            });
            started.SetResult();
            await cancelled.Task;
        });
        Task sent = queue.Enqueue(new("text", 1));
        await Bounded(started.Task);
        Task disposing = queue.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<AggregateException>(() => Bounded(disposing));
        await Bounded(sent);
        Assert.True(sent.IsCompletedSuccessfully);
        Assert.Same(disposing, queue.DisposeAsync().AsTask());
    }

    private static TaskCompletionSource Signal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Bounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
}
