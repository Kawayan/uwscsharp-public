using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerSerialTimerTests
{
    [Fact]
    public async Task SlowCallbackIsNotOverlappedAndSecondLoopIsRejected()
    {
        var timer = new SchedulerSerialTimer(interval: TimeSpan.FromMilliseconds(10));
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Task operation = timer.RunAsync(async token =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            else
            {
                second.TrySetResult();
            }
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                timer.RunAsync(_ => Task.CompletedTask, cancellation.Token));
            await Task.Delay(50);
            Assert.Equal(1, Volatile.Read(ref calls));
            release.SetResult();
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CancelWhileWaitingDoesNotInvokeCallbackAndAllowsRestart()
    {
        var timer = new SchedulerSerialTimer(interval: TimeSpan.FromHours(1));
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        Task operation = timer.RunAsync(_ =>
        {
            calls++;
            return Task.CompletedTask;
        }, cancellation.Token);
        cancellation.Cancel();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.RunAsync(_ => Task.CompletedTask, cancellation.Token);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CallbackFailureStopsLoopWithoutAutomaticRetry()
    {
        var timer = new SchedulerSerialTimer(interval: TimeSpan.FromMilliseconds(1));
        int calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => timer.RunAsync(_ =>
        {
            calls++;
            throw new InvalidDataException("検査用の監視失敗");
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
    }
}
