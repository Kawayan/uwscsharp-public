using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerOwnerHandoffTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealOwnerTerminationRecoversDescendantsBeforeSuccessorAndDoesNotReplayOneShot()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            string directory = Path.Combine(fixture.DirectoryPath, "handoff");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "probe-path.txt"),
                FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe"), timeout.Token);
            string ownerReceipt = Path.Combine(directory, "owner.json");
            Process owner = fixture.StartEditor("--verify-scheduler-handoff-owner", directory, ownerReceipt);
            await WaitReceipt(owner, ownerReceipt, timeout.Token);
            using JsonDocument original = await ProcessReceiptReader.ReadJsonAsync(ownerReceipt, timeout.Token);
            Assert.Equal("Ready", original.RootElement.GetProperty("result").GetString());
            Assert.Equal(owner.Id, original.RootElement.GetProperty("process_id").GetInt32());
            Assert.Equal(3, original.RootElement.GetProperty("checks").GetArrayLength());
            var old = new List<Process>();
            foreach (string key in new[] { "worker", "child", "grandchild" })
            {
                Process process = fixture.Own(original.RootElement.GetProperty(key).GetInt32());
                _ = process.Handle;
                Assert.False(process.HasExited);
                old.Add(process);
            }
            string successorReceipt = Path.Combine(directory, "successor.json");
            Process successor = fixture.StartEditor("--verify-scheduler-handoff-successor", directory, successorReceipt);
            await WaitReceipt(successor, successorReceipt + ".busy", timeout.Token, successorReceipt + ".error");
            Assert.False(owner.HasExited);
            Assert.All(old, process => Assert.False(process.HasExited));
            // 旧本体だけを停止し、子孫の終了は製品のJobと後継の準備へ任せます。
            owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync(timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(directory, "takeover"), "go", timeout.Token);
            await successor.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(successorReceipt + ".error"), File.Exists(successorReceipt + ".error")
                ? await File.ReadAllTextAsync(successorReceipt + ".error", timeout.Token) : "");
            Assert.Equal(0, successor.ExitCode);
            Assert.All(old, process => Assert.True(process.HasExited));
            using JsonDocument result = await ProcessReceiptReader.ReadJsonAsync(successorReceipt, timeout.Token);
            Assert.Equal("Passed", result.RootElement.GetProperty("result").GetString());
            Assert.Equal(successor.Id, result.RootElement.GetProperty("process_id").GetInt32());
            int startAttempts = result.RootElement.GetProperty("start_attempts").GetInt32();
            Assert.True(startAttempts >= 1);
            string[] checks = [.. result.RootElement.GetProperty("checks").EnumerateArray().Select(value => value.GetString()!)];
            Assert.Equal(7, checks.Length);
            Assert.Equal(checks.Length, checks.Distinct().Count());
            output.WriteLine("owner-only-termination;held-process-handles=" + old.Count);
            output.WriteLine($"successor-start-attempts={startAttempts}");
            output.WriteLine(string.Join(Environment.NewLine, checks));
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await fixture.CleanupAsync(primary);
        }
    }

    private static async Task WaitReceipt(Process process, string path, CancellationToken token, string? errorPath = null)
    {
        errorPath ??= path + ".error";
        while (!File.Exists(path))
        {
            Assert.False(File.Exists(errorPath), File.Exists(errorPath) ? await File.ReadAllTextAsync(errorPath, token) : "");
            Assert.False(process.HasExited, "通知前に検証プロセスが終了しました。");
            await Task.Delay(25, token);
        }
    }
}
