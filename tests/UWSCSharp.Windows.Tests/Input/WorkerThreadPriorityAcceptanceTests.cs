using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WorkerThreadPriorityAcceptanceTests
{
    [Fact]
    public async Task AppliesAllSixPrioritiesToTheOwnedExecutionThread()
    {
        // 自身で作成するThreadの優先度だけを検査し、画面・入力を操作しません。
        var finished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var priority = new WindowsWorkerThreadPriority();
                priority.Initialize();
                try
                {
                    foreach (WorkerThreadPriority value in Enum.GetValues<WorkerThreadPriority>())
                    {
                        priority.SetPriority(value);
                        Assert.Equal((int)value, GetThreadPriority(GetCurrentThread()));
                    }
                }
                finally
                {
                    priority.SetPriority(WorkerThreadPriority.Normal);
                }

                finished.SetResult();
            }
            catch (Exception failure)
            {
                finished.SetException(failure);
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            Assert.True(thread.Join(5000));
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern int GetThreadPriority(nint thread);
}
