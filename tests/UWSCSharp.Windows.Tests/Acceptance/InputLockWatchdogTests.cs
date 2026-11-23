using System.Diagnostics;
using System.Globalization;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class InputLockWatchdogTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("hold", false)]
    [InlineData("hold", true)]
    [InlineData("parent-wait", true)]
    public async Task DeadlineStopsUnresponsiveOwnedProcessesWithoutInputBlocking(
        string mode, bool publishReady)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string marker = Path.Combine(Path.GetTempPath(), "uwsc-watchdog-" + Guid.NewGuid() + ".txt");
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        string command = $"\"{executable}\" --process-output-probe {mode} 0 37";
        if (publishReady)
        {
            command += $" \"{marker}\"";
        }

        Process? child = null;
        try
        {
            var elapsed = Stopwatch.StartNew();
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, command, TimeSpan.FromSeconds(3));
            using var root = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = root.SafeHandle;
            Assert.False(root.HasExited);
            if (publishReady)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (!File.Exists(marker))
                {
                    await Task.Delay(10, timeout.Token);
                }

                int id = int.Parse(await File.ReadAllTextAsync(marker, timeout.Token), CultureInfo.InvariantCulture);
                if (mode == "parent-wait")
                {
                    child = Process.GetProcessById(id);
                    _ = child.SafeHandle;
                    Assert.False(child.HasExited);
                }
                else
                {
                    Assert.Equal(root.Id, id);
                }
            }
            else
            {
                Assert.False(File.Exists(marker));
            }

            Assert.True(await watchdog.Completion.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.True(root.HasExited);
            Assert.Equal(1, root.ExitCode);
            if (child is not null)
            {
                Assert.True(child.HasExited);
                Assert.Equal(1, child.ExitCode);
            }

            Assert.InRange(elapsed.Elapsed.TotalSeconds, 3, 15);
            output.WriteLine($"mode={mode} ready={publishReady} seconds={elapsed.Elapsed.TotalSeconds:F3} root_exited=true child_exited={child?.HasExited} input_blocked=false");
        }
        finally
        {
            child?.Dispose();
            File.Delete(marker);
        }
    }
}
