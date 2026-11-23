using System.Collections.Concurrent;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputCleanupTests
{
    [Fact]
    public async Task SharedCleanupPreservesSeparateSettingsAndWindowIds()
    {
        var cleanup = new WindowsSelectiveInputCleanup();
        await using var first = new Run(cleanup: cleanup);
        await using var second = new Run(cleanup: cleanup);
        Value firstId = await first.Root.InvokeAsync(
            BuiltinFunctionNames.HndToId, [Value.Numeric(77)], default);
        Value secondId = await second.Root.InvokeAsync(
            BuiltinFunctionNames.HndToId, [Value.Numeric(88)], default);
        Assert.Equal(1, firstId.Number);
        Assert.Equal(1, secondId.Number);
        await first.StartAsync();
        await second.StartAsync();
        first.Hook.Failures = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => first.Root.DisposeAsync().AsTask());
        Assert.Equal(1, cleanup.PendingCount);
        Assert.Equal(0, (await cleanup.RetryAsync()).PendingCount);
        Assert.False(first.Hook.IsRegistered);
        Assert.True(second.Hook.IsRegistered);
        Assert.Equal(0, second.Hook.Stops);
    }

    [Fact]
    public async Task SharedCleanupRecoversSeparateProviders()
    {
        var cleanup = new WindowsSelectiveInputCleanup();
        await using var first = new Run(cleanup: cleanup);
        await using var second = new Run(cleanup: cleanup);
        await first.StartAsync();
        await second.StartAsync();
        first.Hook.Failures = 2;
        second.Hook.Failures = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => first.Root.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => second.Root.DisposeAsync().AsTask());
        Assert.Equal(2, cleanup.PendingCount);
        WindowsSelectiveInputCleanupResult result = await cleanup.RetryAsync();
        Assert.Equal(0, result.PendingCount);
        Assert.Equal(2, result.Errors.Count);
        Assert.False(first.Hook.IsRegistered);
        Assert.False(second.Hook.IsRegistered);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<Exception>)result.Errors).Clear());
    }

    [Fact]
    public void HostWindowFactoriesAcceptSharedCleanupWithoutCallingHostCallbacks()
    {
        var cleanup = new WindowsSelectiveInputCleanup();
        var commands = new WindowsCommandOptions();
        var sync = WindowsAutomation.WithHostWindows(
            static (_, _) => throw new InvalidOperationException(), null, commands, cleanup);
        var asynchronous = WindowsAutomation.WithAsyncHostWindows(
            static (_, _) => throw new InvalidOperationException(),
            static (_, _) => throw new InvalidOperationException(), commands, cleanup);
        Assert.Same(cleanup, sync.SelectiveInputCleanup);
        Assert.Same(cleanup, asynchronous.SelectiveInputCleanup);
        Assert.Same(commands, sync.Commands);
        Assert.Same(commands, asynchronous.Commands);
    }

    [Fact]
    public void ExistingFactoriesKeepIndependentCleanupInstances()
    {
        var first = WindowsAutomation.WithHostWindows(
            static (_, _) => throw new InvalidOperationException(), null);
        var second = WindowsAutomation.WithHostWindows(
            static (_, _) => throw new InvalidOperationException(), null, new());
        var third = WindowsAutomation.WithAsyncHostWindows(
            static (_, _) => throw new InvalidOperationException(),
            static (_, _) => throw new InvalidOperationException(), new());
        Assert.NotSame(first.SelectiveInputCleanup, second.SelectiveInputCleanup);
        Assert.NotSame(second.SelectiveInputCleanup, third.SelectiveInputCleanup);
    }

    [Fact]
    public void NullCleanupIsRejectedBeforeSessionCreation()
    {
        var provider = new WindowsAutomation { SelectiveInputCleanup = null! };
        Assert.Throws<ArgumentNullException>(() => provider.CreateSession());
        Assert.Throws<ArgumentNullException>(() => WindowsAutomation.WithHostWindows(
            static (_, _) => throw new InvalidOperationException(), null, new(), null!));
        Assert.Throws<ArgumentNullException>(() => WindowsAutomation.WithAsyncHostWindows(
            static (_, _) => throw new InvalidOperationException(),
            static (_, _) => throw new InvalidOperationException(), new(), null!));
    }

    [Fact]
    public async Task UnusedSessionDoesNotCreateARecoveryEntry()
    {
        await using var run = new Run();
        await run.Root.DisposeAsync();
        Assert.Equal(0, run.Cleanup.PendingCount);
        Assert.Equal(0, run.Hook.Stops);
    }

    [Fact]
    public async Task SuccessfulRootCleanupDoesNotRetainAnOwner()
    {
        await using var run = new Run();
        await run.StartAsync();
        await run.Root.DisposeAsync();
        Assert.Equal(0, run.Cleanup.PendingCount);
        WindowsSelectiveInputCleanupResult result = await run.Cleanup.RetryAsync();
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.PendingCount);
    }

    [Fact]
    public async Task ReleasedOwnerIsNotRetainedBecauseOfHistoricalErrors()
    {
        await using var run = new Run();
        await run.StartAsync();
        run.Hook.Failures = 1;
        await Assert.ThrowsAnyAsync<Exception>(() => run.Root.DisposeAsync().AsTask());
        Assert.False(run.Hook.IsRegistered);
        Assert.Equal(0, run.Cleanup.PendingCount);
    }

    [Fact]
    public async Task ProviderRecoversWithoutTheDisposedRootSession()
    {
        await using var run = new Run();
        await run.StartAsync();
        run.Hook.Failures = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => run.Root.DisposeAsync().AsTask());
        Assert.Equal(1, run.Cleanup.PendingCount);
        Assert.True(run.Hook.IsRegistered);
        WindowsSelectiveInputCleanupResult result = await run.Cleanup.RetryAsync();
        Assert.False(run.Hook.IsRegistered);
        Assert.Equal(0, result.PendingCount);
        Assert.Equal(0, run.Cleanup.PendingCount);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(3, run.Hook.Stops);
        Assert.Empty((await run.Cleanup.RetryAsync()).Errors);
    }

    [Fact]
    public async Task AnotherFailureKeepsTheOwnerForALaterRequest()
    {
        await using var run = new Run();
        await run.StartAsync();
        run.Hook.Failures = 4;
        await Assert.ThrowsAnyAsync<Exception>(() => run.Root.DisposeAsync().AsTask());
        WindowsSelectiveInputCleanupResult failed = await run.Cleanup.RetryAsync();
        Assert.NotEmpty(failed.Errors);
        Assert.Equal(1, failed.PendingCount);
        Assert.True(run.Hook.IsRegistered);
        WindowsSelectiveInputCleanupResult recovered = await run.Cleanup.RetryAsync();
        Assert.Equal(0, recovered.PendingCount);
        Assert.NotEmpty(recovered.Errors);
        Assert.False(run.Hook.IsRegistered);
        Assert.Equal(5, run.Hook.Stops);
    }

    [Fact]
    public async Task CompletedRecoveryThroughTheRootIsRemovedFromTheRegistry()
    {
        await using var run = new Run();
        await run.StartAsync();
        run.Hook.Failures = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => run.Root.DisposeAsync().AsTask());
        Assert.Equal(1, run.Cleanup.PendingCount);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            ((ISelectiveInputOwnershipSession)run.Root).RetrySelectiveInputCleanupAsync());
        Assert.Equal(0, run.Cleanup.PendingCount);
        Assert.Empty((await run.Cleanup.RetryAsync()).Errors);
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneAttemptAndRetainNewFailures()
    {
        await using var first = new Run();
        await using var second = new Run(first);
        await first.StartAsync();
        await second.StartAsync();
        first.Hook.Failures = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => first.Root.DisposeAsync().AsTask());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        first.Hook.OnStop = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<WindowsSelectiveInputCleanupResult> retry = first.Cleanup.RetryAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task<WindowsSelectiveInputCleanupResult>[] requests =
                [.. Enumerable.Range(0, 32).Select(_ => first.Cleanup.RetryAsync())];
            Assert.All(requests, request => Assert.Same(retry, request));
            second.Hook.Failures = 2;
            await Assert.ThrowsAnyAsync<Exception>(() => second.Root.DisposeAsync().AsTask());
            Assert.Equal(2, first.Cleanup.PendingCount);
            release.Set();
            Assert.Equal(1, (await retry).PendingCount);
            Assert.True(second.Hook.IsRegistered);
            Assert.Equal(0, (await first.Cleanup.RetryAsync()).PendingCount);
            Assert.False(second.Hook.IsRegistered);
        }
        finally
        {
            release.Set();
            await retry;
        }
    }

    [Fact]
    public async Task SeparateProvidersDoNotRecoverEachOthersOwners()
    {
        await using var first = new Run();
        await using var second = new Run();
        await first.StartAsync();
        await second.StartAsync();
        first.Hook.Failures = 2;
        second.Hook.Failures = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => first.Root.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => second.Root.DisposeAsync().AsTask());
        Assert.Equal(0, (await first.Cleanup.RetryAsync()).PendingCount);
        Assert.Equal(1, second.Cleanup.PendingCount);
        Assert.True(second.Hook.IsRegistered);
    }

    private sealed class Run : IAsyncDisposable
    {
        private readonly ConcurrentQueue<Hook> hooks;

        internal readonly Hook Hook = new();
        internal readonly WindowsAutomation Provider;
        internal readonly IWindowsAutomationSession Root;

        internal Run(Run? shared = null, WindowsSelectiveInputCleanup? cleanup = null)
        {
            hooks = shared?.hooks ?? new ConcurrentQueue<Hook>();
            hooks.Enqueue(Hook);
            Provider = shared?.Provider ?? new WindowsAutomation(
                _ => throw new InvalidOperationException("入力送信を使いません。"),
                swappedMouseButtons: false,
                selectiveInputPlatform: new Platform(),
                selectiveInputFactory: (_, _) => hooks.TryDequeue(out Hook? hook)
                    ? hook : throw new InvalidOperationException("偽フックがありません。"))
            {
                SelectiveInputCleanup = cleanup ?? new()
            };
            Root = Provider.CreateSession();
        }

        internal WindowsSelectiveInputCleanup Cleanup => Provider.SelectiveInputCleanup;

        internal Task StartAsync() =>
            ((ISelectiveInputOwnershipSession)Root)
                .SetSelectiveInputAsync(new(0, 1), default);

        public async ValueTask DisposeAsync()
        {
            Hook.OnStop = null;
            Hook.Failures = 0;
            try
            {
                await Root.DisposeAsync();
            }
            catch
            {
                // 検査済みの終了失敗を再通知せず、残る所有者の回収は必ず試みます。
            }

            await Cleanup.RetryAsync();
        }
    }

    private sealed class Platform : IWindowIdentityPlatform
    {
        public WindowIdentity? ReadIdentity(nint window) => new(1, 2);
    }

    // OSのフック・画面・入力を使わず、所有スレッド上の解除だけを注入します。
    private sealed class Hook : ISelectiveInputHookSession
    {
        private int registered;
        internal int Failures;
        internal int Stops;
        internal Action? OnStop;

        public bool IsRegistered => Volatile.Read(ref registered) != 0;

        public void Start() => Volatile.Write(ref registered, 1);

        public bool Pump() => false;

        public void Wait(AutoResetEvent work, int milliseconds) => work.WaitOne(milliseconds);

        public void Stop()
        {
            Interlocked.Increment(ref Stops);
            OnStop?.Invoke();
            if (Interlocked.Decrement(ref Failures) >= 0)
            {
                throw new InvalidOperationException("解除失敗");
            }

            Volatile.Write(ref registered, 0);
        }

        public void RefreshTargets()
        {
        }
    }
}
