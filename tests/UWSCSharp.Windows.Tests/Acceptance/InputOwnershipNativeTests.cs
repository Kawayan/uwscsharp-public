using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class InputOwnershipNativeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerCanBlockSendAndReleaseInsideMonitoredGuest(bool terminateOwner)
    {
        if (terminateOwner)
        {
            await RunProbeAsync(hold: true);
        }

        await RunProbeAsync(hold: false);
    }

    [Fact]
    public async Task NewOwnerCanBlockAfterPreviousThreadExitsWithoutUnblocking()
    {
        await RunProbeAsync(hold: false, exitOwnerThread: true);
    }

    private async Task RunProbeAsync(bool hold, bool exitOwnerThread = false)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string marker = Path.Combine(Path.GetTempPath(), "uwsc-input-owner-" + Guid.NewGuid() + ".json");
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        try
        {
            string command = $"\"{executable}\" --input-ownership-probe \"{marker}\"" + (hold ? " hold" : "");
            if (exitOwnerThread)
            {
                command += " owner-thread-exit";
            }
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, command, TimeSpan.FromSeconds(hold ? 3 : 10));
            using var target = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = target.SafeHandle;
            // 監視の期限が動き始めてから、対象へ開始を通知します。
            await File.WriteAllTextAsync(marker + ".go", "start");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!File.Exists(marker))
            {
                await Task.Delay(10, deadline.Token);
            }

            await using var stream = new FileStream(marker, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete, 4096,
                FileOptions.Asynchronous);
            using var reader = new StreamReader(stream);
            string receipt = await reader.ReadToEndAsync(deadline.Token);
            output.WriteLine(receipt);
            using var document = JsonDocument.Parse(receipt);
            JsonElement result = document.RootElement;
            Assert.Equal(JsonValueKind.Null, result.GetProperty("failure").ValueKind);
            if (exitOwnerThread)
            {
                Assert.True(result.GetProperty("blocked").GetBoolean());
                Assert.True(result.GetProperty("ownerThreadExited").GetBoolean());
                Assert.False(result.GetProperty("releasedByOwner").GetBoolean());
                Assert.True(result.GetProperty("reacquired").GetBoolean());
                await target.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, target.ExitCode);
                return;
            }

            Assert.True(result.GetProperty("blocked").GetBoolean());
            if (hold)
            {
                Assert.False(result.GetProperty("released").GetBoolean());
                Assert.True(await watchdog.Completion.WaitAsync(TimeSpan.FromSeconds(15)));
                Assert.True(target.HasExited);
                Assert.Equal(1, target.ExitCode);
                return;
            }

            Assert.True(result.GetProperty("sent").GetBoolean());
            Assert.True(result.GetProperty("released").GetBoolean());
            Assert.True(result.GetProperty("otherBlockRejected").GetBoolean());
            Assert.True(result.GetProperty("initialKeyUp").GetBoolean());
            Assert.False(result.GetProperty("otherKeyDown").GetBoolean());
            Assert.True(result.GetProperty("ownerKeyDown").GetBoolean());
            Assert.True(result.GetProperty("ownerKeyUp").GetBoolean());
            Assert.True(result.GetProperty("nextOwnerKeyDown").GetBoolean());
            Assert.True(result.GetProperty("reacquired").GetBoolean());
            await target.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, target.ExitCode);
        }
        finally
        {
            File.Delete(marker);
            File.Delete(marker + ".go");
            File.Delete(marker + ".pending");
        }
    }
}
