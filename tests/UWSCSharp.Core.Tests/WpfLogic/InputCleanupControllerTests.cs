using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class InputCleanupControllerTests
{
    [Fact]
    public async Task PendingRefreshDoesNotRetryAndCompletionRestoresStart()
    {
        int pending = 1;
        int retries = 0;
        var controller = new InputCleanupController(() => pending, () =>
        {
            retries++;
            return Task.FromResult(new InputCleanupSnapshot([], 0));
        });
        await controller.RefreshAsync();
        Assert.False(controller.CanStart);
        Assert.True(controller.CanRetry);
        Assert.Equal(1, controller.PendingCount);
        pending = 0;
        await controller.RefreshAsync();
        Assert.True(controller.CanStart);
        Assert.False(controller.CanRetry);
        Assert.Equal(0, retries);
    }

    [Fact]
    public async Task SlowSynchronousCountRunsAwayFromCallerAndBlocksStart()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var controller = new InputCleanupController(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return 0;
        }, () => throw new InvalidOperationException());
        Task<InputCleanupSnapshot> checking = controller.RefreshAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(controller.IsBusy);
            Assert.False(controller.CanStart);
            Assert.False(controller.CanRetry);
            Assert.Same(checking, controller.RefreshAsync());
        }
        finally
        {
            release.Set();
        }

        await checking;
        Assert.True(controller.CanStart);
    }

    [Fact]
    public async Task RetrySharesOneRequestAndKeepsHistoricalErrorsSeparate()
    {
        var completion = new TaskCompletionSource<InputCleanupSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new IOException("検証用の過去の解除失敗");
        int retries = 0;
        var controller = new InputCleanupController(() => 1, () =>
        {
            Interlocked.Increment(ref retries);
            return completion.Task;
        }, initialCount: 1);
        Task<InputCleanupSnapshot> first = controller.RetryAsync();
        Assert.Same(first, controller.RetryAsync());
        Assert.Same(first, controller.RefreshAsync());
        Assert.False(controller.CanStart);
        Assert.False(controller.CanRetry);
        completion.SetResult(new InputCleanupSnapshot([error], 0));
        InputCleanupSnapshot result = await first;
        Assert.Same(error, Assert.Single(result.Errors));
        Assert.Equal(0, controller.PendingCount);
        Assert.True(controller.CanStart);
        Assert.Equal(1, retries);
        await controller.RetryAsync();
        Assert.Equal(1, retries);
    }

    [Fact]
    public async Task SlowSynchronousRetryDoesNotBlockCaller()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var controller = new InputCleanupController(() => 1, () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Task.FromResult(new InputCleanupSnapshot([], 0));
        }, initialCount: 1);
        Task<InputCleanupSnapshot> retrying = controller.RetryAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(controller.IsBusy);
        }
        finally
        {
            release.Set();
        }

        await retrying;
        Assert.True(controller.CanStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureKeepsStartBlockedUntilStateIsKnown(bool retryFailure)
    {
        var failure = new IOException("検証用の状態照会失敗");
        bool fail = true;
        var controller = new InputCleanupController(
            () => fail ? throw failure : 0,
            () => fail ? throw failure : Task.FromResult(new InputCleanupSnapshot([], 0)),
            initialCount: 2);
        InputCleanupSnapshot result = await (retryFailure
            ? controller.RetryAsync() : controller.RefreshAsync());
        Assert.Same(failure, Assert.Single(result.Errors));
        Assert.Equal(2, controller.PendingCount);
        Assert.False(controller.HasKnownCount);
        Assert.False(controller.CanStart);
        Assert.True(controller.CanRetry);
        fail = false;
        await controller.RefreshAsync();
        Assert.True(controller.HasKnownCount);
        Assert.True(controller.CanStart);
    }

    [Fact]
    public async Task UnknownInitialStateCannotStartBeforeRefresh()
    {
        var controller = new InputCleanupController(() => 0,
            () => throw new InvalidOperationException(), initialCount: null);
        Assert.False(controller.HasKnownCount);
        Assert.False(controller.CanStart);
        await controller.RefreshAsync();
        Assert.True(controller.CanStart);
    }

    [Fact]
    public async Task PendingRetryNeedsAnotherExplicitRequest()
    {
        int retries = 0;
        var controller = new InputCleanupController(() => 1,
            () => Task.FromResult(new InputCleanupSnapshot([], ++retries == 1 ? 1 : 0)),
            initialCount: 1);
        await controller.RetryAsync();
        Assert.Equal(1, retries);
        Assert.False(controller.CanStart);
        Assert.True(controller.CanRetry);
        await controller.RetryAsync();
        Assert.Equal(2, retries);
        Assert.True(controller.CanStart);
    }
}
