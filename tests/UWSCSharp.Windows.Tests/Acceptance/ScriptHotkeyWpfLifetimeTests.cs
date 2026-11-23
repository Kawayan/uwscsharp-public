using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class ScriptHotkeyWpfLifetimeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SameWpfWindowReleasesModifierHooksAndDeliversOnlyTheNextRun()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string receipt = Path.Combine(Path.GetTempPath(), "uwsc-wpf-hotkey-lifetime-" + Guid.NewGuid() + ".json");
        string executable = FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
        try
        {
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, $"\"{executable}\" --verify-script-hotkey-lifetime \"{receipt}\"",
                TimeSpan.FromSeconds(30));
            using var process = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = process.SafeHandle;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(28));
            Assert.True(File.Exists(receipt), "ホットキー寿命の検証結果がありません。");
            string text = await File.ReadAllTextAsync(receipt);
            output.WriteLine(text);
            using var json = JsonDocument.Parse(text);
            JsonElement result = json.RootElement;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("PASS", result.GetProperty("result").GetString());
            Assert.Equal(process.Id, result.GetProperty("process_id").GetInt32());
            Assert.True(result.GetProperty("window_shown").GetBoolean());
            Assert.True(result.GetProperty("input_injection").GetBoolean());
            long handle = result.GetProperty("handle").GetInt64();
            Assert.NotEqual(0, handle);
            JsonElement[] checks = [.. result.GetProperty("checks").EnumerateArray()];
            Assert.Equal(12, checks.Length);
            string[] endings = ["normal", "retry", "error", "retry", "cancel", "retry"];
            for (int index = 0; index < checks.Length; index++)
            {
                JsonElement check = checks[index];
                bool modifierOnly = index >= 6;
                string ending = endings[index % 6];
                Assert.Equal(index + 1, check.GetProperty("generation").GetInt32());
                Assert.Equal(modifierOnly, check.GetProperty("modifier_only").GetBoolean());
                Assert.Equal(ending, check.GetProperty("ending").GetString());
                Assert.Equal(ending == "cancel" ? 130 : ending == "error" ? 1 : 0,
                    check.GetProperty("exit").GetInt32());
                Assert.Equal(handle, check.GetProperty("handle").GetInt64());
                Assert.Equal(1, check.GetProperty("registered").GetInt32());
                Assert.Equal(1, check.GetProperty("unregistered").GetInt32());
                Assert.Equal(1, check.GetProperty("shutdowns").GetInt32());
                Assert.Equal(modifierOnly ? 1 : 0, check.GetProperty("installed").GetInt32());
                Assert.Equal(modifierOnly ? 1 : 0, check.GetProperty("removed").GetInt32());
                Assert.False(check.GetProperty("callback_rooted").GetBoolean());
                Assert.False(check.GetProperty("source_thread_alive").GetBoolean());
                Assert.Equal(1, check.GetProperty("callback_count").GetInt32());
                Assert.True(check.GetProperty("starts_restored").GetBoolean());
                if (modifierOnly)
                {
                    Assert.NotEqual(0, check.GetProperty("install_thread").GetInt32());
                    Assert.Equal(check.GetProperty("install_thread").GetInt32(),
                        check.GetProperty("remove_thread").GetInt32());
                }
            }
        }
        finally
        {
            if (File.Exists(receipt))
            {
                output.WriteLine(await File.ReadAllTextAsync(receipt));
            }
            File.Delete(receipt);
        }
    }
}
