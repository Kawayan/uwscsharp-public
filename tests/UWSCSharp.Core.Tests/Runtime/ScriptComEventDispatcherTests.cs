using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptComEventDispatcherTests
{
    [Fact]
    public async Task CancellationBeforeDispatchDoesNotRunProcedure()
    {
        var dispatcher = new ScriptComEventDispatcher();
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        Task<IReadOnlyList<Value>> pending = dispatcher.EnqueueAsync(() =>
        {
            calls++;
            return ValueTask.FromResult<IReadOnlyList<Value>>([]);
        }, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await dispatcher.DispatchOneAsync();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationAfterDispatchWaitsForProcedureToFinish()
    {
        var dispatcher = new ScriptComEventDispatcher();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<Value>> pending = dispatcher.EnqueueAsync(async () =>
        {
            entered.SetResult();
            await finish.Task;
            return [];
        }, cancellation.Token).AsTask();
        Task dispatch = dispatcher.DispatchOneAsync().AsTask();
        await entered.Task;
        cancellation.Cancel();
        Assert.False(pending.IsCompleted);
        finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task CloseCancelsPendingAndRejectsLaterNotifications()
    {
        var dispatcher = new ScriptComEventDispatcher();
        Task<IReadOnlyList<Value>> pending = dispatcher.EnqueueAsync(
            () => ValueTask.FromResult<IReadOnlyList<Value>>([]), default).AsTask();
        dispatcher.Close();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.EnqueueAsync(
            () => ValueTask.FromResult<IReadOnlyList<Value>>([]), default).AsTask());
    }
}
