using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class InputLockWatchdogOwnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerDeathClosesOnlyTheOptedInJob(bool killOnClose)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string receipt = Path.Combine(Path.GetTempPath(), "uwsc-watchdog-owner-" + Guid.NewGuid());
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using CapturedProcess owner = await CapturedProcess.StartAsync(executable,
            $"\"{executable}\" --watchdog-owner-probe \"{receipt}\" {killOnClose}", timeout.Token);
        Task<byte[]> output = owner.ReadOutputAsync(64 * 1024, timeout.Token);
        try
        {
            while (!File.Exists(receipt))
            {
                await Task.Delay(10, timeout.Token);
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, timeout.Token));
            using var target = Process.GetProcessById(document.RootElement.GetProperty("target").GetInt32());
            using var child = Process.GetProcessById(document.RootElement.GetProperty("child").GetInt32());
            _ = target.SafeHandle;
            _ = child.SafeHandle;
            Assert.False(target.HasExited);
            Assert.False(child.HasExited);
            using var watcher = Process.GetProcessById(checked((int)owner.Id));
            // 木全体の停止を使わず、監視役だけを終了させます。
            watcher.Kill(entireProcessTree: false);
            await watcher.WaitForExitAsync(timeout.Token);
            if (killOnClose)
            {
                await Task.WhenAll(target.WaitForExitAsync(), child.WaitForExitAsync())
                    .WaitAsync(TimeSpan.FromSeconds(5));
                await output.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                await Task.Delay(300, timeout.Token);
                Assert.False(target.HasExited);
                Assert.False(child.HasExited);
            }
        }
        finally
        {
            var cleanup = new InvalidOperationException("所有する模擬プロセスの後始末");
            await owner.StopAfterFailureAsync(cleanup);
            await output.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(cleanup.Data);
            File.Delete(receipt);
            File.Delete(receipt + ".child");
        }
    }
}
