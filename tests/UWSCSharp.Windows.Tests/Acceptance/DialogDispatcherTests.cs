using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class DialogDispatcherTests(ITestOutputHelper output)
{
    [Fact]
    public async Task UsesAnOwnedStaAndDestroysItsMessageWindow()
    {
        var dispatcher = new NativeDialogDispatcher();
        nint window = 0;
        try
        {
            (int CurrentManagedThreadId, ApartmentState, nint) actual = await dispatcher.InvokeAsync(() =>
                (Environment.CurrentManagedThreadId,
                    Thread.CurrentThread.GetApartmentState(), FindOwnedQueue()));
            Assert.Equal(dispatcher.ManagedThreadId, actual.Item1);
            Assert.Equal(ApartmentState.STA, actual.Item2);
            window = actual.Item3;
            Assert.NotEqual(0, window);
            Assert.False(Native.IsWindowVisible(window));
        }
        finally
        {
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.False(dispatcher.IsAlive);
        Assert.False(Native.IsWindow(window));
        await dispatcher.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dispatcher.InvokeAsync(() => 1));
    }

    [Fact]
    public async Task PreservesOperationFailureAndContinuesServingRequests()
    {
        await using var dispatcher = new NativeDialogDispatcher();
        var expected = new InvalidOperationException("UI操作の失敗");
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.InvokeAsync<int>(() => throw expected));
        Assert.Same(expected, actual);
        Assert.Equal(42, await dispatcher.InvokeAsync(() => 42));
    }

    [Fact]
    public async Task CancellationBeforeSubmissionDoesNotRunTheOperation()
    {
        await using var dispatcher = new NativeDialogDispatcher();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.InvokeAsync(() => ++calls, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationWhileQueuedDoesNotRunTheOperation()
    {
        await using var dispatcher = new NativeDialogDispatcher();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = dispatcher.InvokeAsync(() =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return 0;
        });
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task second = dispatcher.InvokeAsync(() => ++calls, cancellation.Token);
            cancellation.Cancel();
            release.Set();
            await first;
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.Equal(0, calls);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ConcurrentRequestsCompleteOnceOnTheOwnerThread()
    {
        await using var dispatcher = new NativeDialogDispatcher();
        int calls = 0;
        Task<int>[] requests = [.. Enumerable.Range(0, 12000).Select(index =>
            dispatcher.InvokeAsync(() =>
            {
                Assert.Equal(dispatcher.ManagedThreadId, Environment.CurrentManagedThreadId);
                calls++;
                return index;
            }))];
        int[] values = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(Enumerable.Range(0, requests.Length), values);
        Assert.Equal(requests.Length, calls);
    }

    [Fact]
    public async Task DisposalBeforeReadyWaitsForTheNativeThreadToExit()
    {
        var dispatcher = new NativeDialogDispatcher();
        await dispatcher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(dispatcher.IsAlive);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task DisposalFromTheOwnerOperationIsRejectedWithoutStoppingTheQueue()
    {
        await using var dispatcher = new NativeDialogDispatcher();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.InvokeAsync(() => dispatcher.DisposeAsync()));
        Assert.Equal(7, await dispatcher.InvokeAsync(() => 7));
    }

    [Fact]
    public async Task AcceptedWorkCompletesBeforeDisposalReturns()
    {
        var dispatcher = new NativeDialogDispatcher();
        await dispatcher.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        int calls = 0;
        Task<int>[] requests = [.. Enumerable.Range(0, 100).Select(_ =>
            dispatcher.InvokeAsync(() => ++calls))];
        Task firstDispose = dispatcher.DisposeAsync().AsTask();
        Task secondDispose = dispatcher.DisposeAsync().AsTask();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(requests.Length, calls);
        Assert.False(dispatcher.IsAlive);
    }

    [Fact]
    public async Task AModalNativeLoopStillReceivesQueuedUiOperations()
    {
        await using var dispatcher = new NativeDialogDispatcher();
        uint thread = await dispatcher.InvokeAsync(Native.GetCurrentThreadId);
        string title = "uwsc-owned-modal-" + Guid.NewGuid();
        Task<int> modal = dispatcher.InvokeAsync(() => MessageBoxW(0, "owned", title, 0));
        nint window = 0;
        try
        {
            var until = System.Diagnostics.Stopwatch.StartNew();
            while (window == 0 && until.Elapsed < TimeSpan.FromSeconds(10))
            {
                Native.EnumWindows((handle, _) =>
                {
                    uint owner = Native.GetWindowThreadProcessId(handle, out uint process);
                    var text = new StringBuilder(128);
                    Native.GetWindowTextW(handle, text, text.Capacity);
                    if (owner == thread && process == Environment.ProcessId && text.ToString() == title
                        && Native.IsWindowVisible(handle))
                    {
                        window = handle;
                    }

                    return true;
                }, 0);
                await Task.Delay(25);
            }

            Assert.NotEqual(0, window);
            output.WriteLine($"dialog={window} visible={Native.IsWindowVisible(window)} enabled={Native.IsWindowEnabled(window)}");
            Assert.True(await dispatcher.InvokeAsync(() =>
            {
                output.WriteLine($"modal-close-operation thread={Environment.CurrentManagedThreadId}");
                return Native.PostMessageW(window, 0x10, 0, 0);
            }).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, await modal.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            // 失敗時も所有するダイアログだけを閉じ、UI threadを待機したまま残しません。
            if (window != 0 && Native.IsWindow(window))
            {
                output.WriteLine("modal-cleanup-fallback");
                Native.PostMessageW(window, 0x10, 0, 0);
            }

            await modal.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static nint FindOwnedQueue()
    {
        uint thread = Native.GetCurrentThreadId();
        nint current = 0;
        while ((current = FindWindowExW(-3, current, null, null)) != 0)
        {
            uint owner = Native.GetWindowThreadProcessId(current, out uint process);
            if (owner == thread && process == Environment.ProcessId)
            {
                return current;
            }
        }

        return 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint parent, nint after, string? className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string message, string title, uint flags);

}
