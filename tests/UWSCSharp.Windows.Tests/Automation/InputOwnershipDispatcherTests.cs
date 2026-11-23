using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class InputOwnershipDispatcherTests
{
    [Fact]
    public async Task OwnsOperationsOnOneThreadAndReleasesOnce()
    {
        var calls = new List<(string Operation, int Thread)>();
        var backend = new Backend(operation =>
            calls.Add((operation, Environment.CurrentManagedThreadId)));
        var dispatcher = new InputOwnershipDispatcher(backend);
        await dispatcher.SetBlockedAsync(true);
        await dispatcher.SetBlockedAsync(true);
        await dispatcher.SendAsync(default);
        await dispatcher.DisposeAsync();
        await dispatcher.DisposeAsync();
        Assert.Equal(["block", "send", "release"], calls.Select(c => c.Operation));
        Assert.Single(calls.Select(c => c.Thread).Distinct());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.SendAsync(default));
    }

    [Fact]
    public async Task FailedBlockDoesNotBecomeOwnedAndCanBeRetried()
    {
        int blocks = 0;
        int releases = 0;
        var backend = new Backend(operation =>
        {
            if (operation == "block" && ++blocks == 1)
            {
                throw new InvalidOperationException("禁止失敗");
            }

            if (operation == "release")
            {
                releases++;
            }
        });
        var dispatcher = new InputOwnershipDispatcher(backend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.SetBlockedAsync(true));
        await dispatcher.SetBlockedAsync(false);
        Assert.Equal(0, releases);
        await dispatcher.SetBlockedAsync(true);
        await dispatcher.DisposeAsync();
        Assert.Equal(2, blocks);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task ReleaseFailureIsReportedAndRetriedOnShutdown()
    {
        int releases = 0;
        var dispatcher = new InputOwnershipDispatcher(new Backend(operation =>
        {
            if (operation == "release" && ++releases == 1)
            {
                throw new InvalidOperationException("解除失敗");
            }
        }));
        await dispatcher.SetBlockedAsync(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.SetBlockedAsync(false));
        await dispatcher.DisposeAsync();
        Assert.Equal(2, releases);
    }

    [Fact]
    public async Task ShutdownReportsReleaseFailureOnEveryWait()
    {
        var dispatcher = new InputOwnershipDispatcher(new Backend(operation =>
        {
            if (operation == "release")
            {
                throw new InvalidOperationException("解除失敗");
            }
        }));
        await dispatcher.SetBlockedAsync(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task SendFailureDoesNotLoseOwnership()
    {
        var calls = new List<string>();
        var dispatcher = new InputOwnershipDispatcher(new Backend(operation =>
        {
            calls.Add(operation);
            if (operation == "send")
            {
                throw new InvalidOperationException("送信失敗");
            }
        }));
        await dispatcher.SetBlockedAsync(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.SendAsync(default));
        await dispatcher.DisposeAsync();
        Assert.Equal(["block", "send", "release"], calls);
    }

    [Fact]
    public async Task ShutdownRejectsQueuedInputAndWaitsForActiveInput()
    {
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int sends = 0;
        var dispatcher = new InputOwnershipDispatcher(new Backend(operation =>
        {
            if (operation == "send")
            {
                sends++;
                entered.Set();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(5)));
            }
        }));
        Task active = dispatcher.SendAsync(default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task queued = dispatcher.SendAsync(default);
            Task shutdown = dispatcher.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            resume.Set();
            await active;
            await shutdown;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
            Assert.Equal(1, sends);
        }
        finally
        {
            resume.Set();
            await dispatcher.DisposeAsync();
        }
    }

    private sealed class Backend(Action<string> action) : IInputOwnershipBackend
    {
        public void SetBlocked(bool blocked) => action(blocked ? "block" : "release");

        public void Send(Native.Input input) => action("send");
    }
}
