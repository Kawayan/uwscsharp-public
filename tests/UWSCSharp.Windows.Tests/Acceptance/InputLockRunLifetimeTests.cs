using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class InputLockRunLifetimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("normal", "completed")]
    [InlineData("exit", "completed")]
    [InlineData("error", "script-error")]
    [InlineData("cancel", "cancelled")]
    public async Task RunCanReleaseAndReacquireWithoutProcessExit(string ending, string outcome)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string receipt = Path.Combine(Path.GetTempPath(), "uwsc-input-run-" + Guid.NewGuid() + ".json");
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        try
        {
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, $"\"{executable}\" --input-lock-run-probe \"{receipt}\" {ending}",
                TimeSpan.FromSeconds(15));
            using var process = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = process.SafeHandle;
            await File.WriteAllTextAsync(receipt + ".go", "go");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(receipt))
            {
                Assert.False(process.HasExited);
                await Task.Delay(10, timeout.Token);
            }
            string text = await File.ReadAllTextAsync(receipt, timeout.Token);
            output.WriteLine(text);
            using var document = JsonDocument.Parse(text);
            JsonElement result = document.RootElement;
            Assert.False(process.HasExited);
            Assert.Equal(process.Id, result.GetProperty("process_id").GetInt32());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("failure").ValueKind);
            Assert.Equal(outcome, result.GetProperty("outcome").GetString());
            foreach (string flag in new[] { "firstDown", "contenderRejected", "firstUp", "secondDown", "secondContenderRejected", "secondUp" })
            {
                Assert.True(result.GetProperty(flag).GetBoolean(), flag);
            }
            await File.WriteAllTextAsync(receipt + ".exit", "exit");
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            foreach (string suffix in new[] { "", ".pending", ".go", ".exit" })
            {
                File.Delete(receipt + suffix);
            }
        }
    }
}
