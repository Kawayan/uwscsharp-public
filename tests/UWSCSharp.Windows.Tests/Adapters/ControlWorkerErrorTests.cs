using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ControlWorkerErrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineIsIdentifiedForWaitingAndCooperativeWorker(
        bool cooperative)
    {
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        try
        {
            TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                NativeControlWorker.RunAsync(token =>
                {
                    try
                    {
                        if (cooperative)
                        {
                            token.WaitHandle.WaitOne();
                            token.ThrowIfCancellationRequested();
                        }
                        else
                        {
                            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                        }

                        return 7;
                    }
                    finally
                    {
                        finished.Set();
                    }
                }, default, TimeSpan.FromMilliseconds(100)));
            Assert.True(WindowsOperationErrors.Contains(failure));
        }
        finally
        {
            release.Set();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(7, await NativeControlWorker.RunAsync(_ => 7, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameTypeOperationFailureIsNotMistakenForWorkerDeadline(
        bool cancellation)
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Exception primary = cancellation
            ? new OperationCanceledException(cancel.Token)
            : new TimeoutException("backend内部の同型障害");
        Exception actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeControlWorker.RunAsync<int>(_ => throw primary,
                default, TimeSpan.FromSeconds(5)));
        Assert.Same(primary, actual);
        Assert.False(WindowsOperationErrors.Contains(actual));
        if (actual is OperationCanceledException cancelled)
        {
            Assert.Equal(cancel.Token, cancelled.CancellationToken);
        }

        Assert.Equal(7, await NativeControlWorker.RunAsync(_ => 7, default));
    }
}
