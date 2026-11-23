using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ProviderActionOperationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OwnWindowCanServiceUiaCallsWhileWaiting(int request)
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", 6, 10);
        Assert.True(await UiaItemActions.InvokeAsync(fixture.Root, "target",
            1 | 512, 1, request, default));
        Assert.Equal((nint)request, Native.SendMessageW(button, 0xf0, 0, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task OwnerWaitDispatchesProviderSynchronousMessagesOnOwner()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint button = fixture.Child("BUTTON", "target", 0, 10);
        uint owner = Native.GetCurrentThreadId();
        uint callback = 0;
        fixture.Clicked = (_, _) => callback = Native.GetCurrentThreadId();
        Assert.Equal(7, await ProviderActionOperation.RunAsync(fixture.Root, token =>
        {
            Assert.NotEqual(owner, Native.GetCurrentThreadId());
            token.ThrowIfCancellationRequested();
            Native.SendMessageW(button, 0xf5, 0, 0);
            return 7;
        }, default));
        Assert.Equal(owner, callback);
    }

    [Fact]
    public async Task TimeoutKeepsWorkerAliveUntilOperationFinishes()
    {
        using var fixture = new ButtonControlTests.Fixture();
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Throws<TimeoutException>(() => ProviderActionOperation.RunAsync(
                fixture.Root, token =>
                {
                    try
                    {
                        release.Wait();
                        return 1;
                    }
                    finally
                    {
                        finished.SetResult();
                    }
                }, default, TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult());
            Assert.False(finished.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task QuitIsPreservedAndCancelsProviderOperation()
    {
        using var fixture = new ButtonControlTests.Fixture();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uint owner = Native.GetCurrentThreadId();
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                ProviderActionOperation.RunAsync(fixture.Root, token =>
                {
                    try
                    {
                        Assert.True(PostThreadMessageW(owner, 0x12, 123, 0));
                        token.WaitHandle.WaitOne();
                        token.ThrowIfCancellationRequested();
                        return 1;
                    }
                    finally
                    {
                        finished.SetResult();
                    }
                }, default).GetAwaiter().GetResult());
        }
        finally
        {
            bool preserved = false;
            while (PeekMessageW(out Message message, 0, 0, 0, 1))
            {
                if (message.Id == 0x12)
                {
                    Assert.Equal((nuint)123, message.WParam);
                    preserved = true;
                    break;
                }

                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
            Assert.True(preserved);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Native.Point Point;
        internal uint Private;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint thread,
        uint message, nuint parameter, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out Message message,
        nint window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);
}
