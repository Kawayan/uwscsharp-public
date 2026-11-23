using Xunit;

namespace UWSCSharp.Windows.Tests;

// 非同期の待ち行列だけを確認し、COM・音声入力・画面は使用しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionControlQueueTests
{
    [Fact]
    public async Task SiblingRequestsSerializeAndCancellationDoesNotLeakQueue()
    {
        var queue = new SpeechRecognitionControlQueue();
        await queue.RunAsync(async () =>
        {
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task first = queue.RunAsync(() => resume.Task, default);
            using var cancellation = new CancellationTokenSource();
            Task cancelled = queue.RunAsync(() => Task.CompletedTask, cancellation.Token);
            Task second = queue.RunAsync(() => Task.CompletedTask, default);
            try
            {
                Assert.False(second.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
                resume.TrySetResult();
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                resume.TrySetResult();
            }
        }, default).WaitAsync(TimeSpan.FromSeconds(10));
        await queue.RunAsync(() => Task.CompletedTask, default);
    }

    [Fact]
    public async Task OuterWaitsForStartedChildAndItsNestedRequest()
    {
        var queue = new SpeechRecognitionControlQueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? child = null;
        Task outer = queue.RunAsync(async () =>
        {
            child = queue.RunAsync(async () =>
            {
                entered.TrySetResult();
                await resume.Task;
                await queue.RunAsync(() => Task.CompletedTask, default);
            }, default);
            await entered.Task;
        }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(outer.IsCompleted);
            Task outside = queue.RunAsync(() => Task.CompletedTask, default);
            Assert.False(outside.IsCompleted);
            resume.TrySetResult();
            await Task.WhenAll(outer, outside).WaitAsync(TimeSpan.FromSeconds(10));
            await child!;
        }
        finally
        {
            resume.TrySetResult();
        }
    }

    [Fact]
    public async Task RetainedCompletedContextCannotBypassNewRoot()
    {
        var queue = new SpeechRecognitionControlQueue();
        ExecutionContext? retained = null;
        await queue.RunAsync(() =>
        {
            retained = ExecutionContext.Capture();
            return Task.CompletedTask;
        }, default);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocking = queue.RunAsync(() => resume.Task, default);
        Task? delayed = null;
        try
        {
            ExecutionContext.Run(retained!, _ =>
                delayed = queue.RunAsync(() => Task.CompletedTask, default), null);
            Assert.False(delayed!.IsCompleted);
            resume.TrySetResult();
            await Task.WhenAll(blocking, delayed).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            resume.TrySetResult();
        }
    }
}
