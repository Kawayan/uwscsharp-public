using System.Collections.Concurrent;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 偽の禁止・送信だけで共有資源の寿命を検証します。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class InputOwnershipRunTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncGuardTracksChildInputBeforeRestorationFailure(bool failRestore)
    {
        var backend = new Backend();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("非同期のホットキー復元失敗");
        int calls = 0;
        await using IWindowsAutomationSession root = new WindowsAutomation(
            _ => throw new InvalidOperationException("所有者を迂回しました。"),
            swappedMouseButtons: false, inputOwnershipFactory: () => backend,
            asyncKeyboardInputGuard: async (key, send) =>
            {
                Assert.Equal(65, key);
                calls++;
                entered.SetResult();
                await resume.Task;
                await send();
                if (failRestore)
                {
                    throw failure;
                }
            }).CreateSession();
        await using IWindowsAutomationSession child = root.Fork();
        await Ownership(root).SetInputBlockedAsync(true);
        Task sending = child.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(65), Value.Numeric(1)], default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(sending.IsCompleted);
        Assert.Equal(["block"], [.. backend.Calls]);
        resume.SetResult();
        if (failRestore)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => sending));
        }
        else
        {
            await sending;
        }

        await child.DisposeAsync();
        Assert.Equal(1, calls);
        Assert.Equal(["block", "down", "up"], [.. backend.Calls]);
        Assert.Single(backend.Threads.Distinct());
    }

    [Fact]
    public async Task WorkerExitReleasesItsKeyButKeepsRunBlocked()
    {
        var backend = new Backend();
        await using IWindowsAutomationSession root = Create(backend);
        IWindowsAutomationSession child = root.Fork();
        await Ownership(child).SetInputBlockedAsync(true);
        await child.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(65), Value.Numeric(1)], default);
        await child.DisposeAsync();
        Assert.Equal(["block", "down", "up"], [.. backend.Calls]);
        await root.DisposeAsync();
        Assert.Equal(["block", "down", "up", "release"], [.. backend.Calls]);
        Assert.Single(backend.Threads.Distinct());
    }

    [Fact]
    public async Task RootReleasesBeforeWaitingForChildAndStillAllowsCleanupUp()
    {
        var backend = new Backend();
        IWindowsAutomationSession root = Create(backend);
        IWindowsAutomationSession child = root.Fork();
        await Ownership(root).SetInputBlockedAsync(true);
        await child.InvokeAsync(BuiltinFunctionNames.Kbd,
            [Value.Numeric(65), Value.Numeric(1)], default);
        Task stopping = root.DisposeAsync().AsTask();
        try
        {
            await backend.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => Ownership(child).SetInputBlockedAsync(true));
        }
        finally
        {
            await child.DisposeAsync();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(["block", "down", "release", "up"], [.. backend.Calls]);
    }

    [Fact]
    public async Task SeparateRunsDoNotShareOwnership()
    {
        var firstBackend = new Backend();
        var secondBackend = new Backend();
        await using IWindowsAutomationSession first = Create(firstBackend);
        await using IWindowsAutomationSession second = Create(secondBackend);
        await Ownership(first).SetInputBlockedAsync(true);
        await Ownership(second).SetInputBlockedAsync(true);
        await first.DisposeAsync();
        Assert.Equal(["block"], [.. secondBackend.Calls]);
        await second.DisposeAsync();
        Assert.Equal(["block", "release"], [.. secondBackend.Calls]);
        Assert.NotEqual(firstBackend.Threads.First(), secondBackend.Threads.First());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootDisposalPreservesReleaseFailureAndRetriesAtOwnerShutdown(bool cancel)
    {
        var backend = new Backend { FailRelease = true };
        using var stop = new CancellationTokenSource();
        IWindowsAutomationSession root = Create(backend);
        root.ConfigurePause(null, stop.Token);
        await Ownership(root).SetInputBlockedAsync(true);
        if (cancel)
        {
            await stop.CancelAsync();
        }

        AggregateException error = await Assert.ThrowsAsync<AggregateException>(
            () => root.DisposeAsync().AsTask());
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(error.InnerExceptions, item => Assert.Equal("解除失敗", item.Message));
        Assert.Equal(["block", "release", "release"], [.. backend.Calls]);
    }

    [Fact]
    public async Task OnlyRootCancellationReleasesSharedOwnership()
    {
        var backend = new Backend();
        using var rootStop = new CancellationTokenSource();
        using var childStop = new CancellationTokenSource();
        await using IWindowsAutomationSession root = Create(backend);
        root.ConfigurePause(null, rootStop.Token);
        IWindowsAutomationSession child = root.Fork();
        child.ConfigurePause(null, childStop.Token);
        await Ownership(child).SetInputBlockedAsync(true);
        await childStop.CancelAsync();
        await Ownership(root).SetInputBlockedAsync(true);
        Assert.Equal(["block"], [.. backend.Calls]);
        await rootStop.CancelAsync();
        await backend.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => Ownership(child).SetInputBlockedAsync(true));
        await child.DisposeAsync();
        await root.DisposeAsync();
        Assert.Equal(["block", "release"], [.. backend.Calls]);
    }

    private static IWindowsAutomationSession Create(Backend backend) =>
        new WindowsAutomation(_ => throw new InvalidOperationException("配送を迂回しました。"),
            swappedMouseButtons: false, inputOwnershipFactory: () => backend).CreateSession();

    private static IInputOwnershipSession Ownership(IWindowsAutomationSession session) =>
        Assert.IsAssignableFrom<IInputOwnershipSession>(session);

    private sealed class Backend : IInputOwnershipBackend
    {
        internal bool FailRelease
        {
            get; init;
        }

        internal ConcurrentQueue<string> Calls { get; } = new();

        internal ConcurrentQueue<int> Threads { get; } = new();

        internal TaskCompletionSource Released
        {
            get;
        } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetBlocked(bool blocked)
        {
            Record(blocked ? "block" : "release");
            if (!blocked)
            {
                if (FailRelease)
                {
                    throw new InvalidOperationException("解除失敗");
                }

                Released.TrySetResult();
            }
        }

        public void Send(Native.Input input) =>
            Record((input.Data.Keyboard.Flags & 2) == 0 ? "down" : "up");

        private void Record(string operation)
        {
            Calls.Enqueue(operation);
            Threads.Enqueue(Environment.CurrentManagedThreadId);
        }
    }
}
