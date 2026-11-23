using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerOverlappingHandoffTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SuccessorRecoversLiveOldWorkerTreeBeforeAcceptingAndRejectsOldCompletion()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            string directory = Path.Combine(fixture.DirectoryPath, "overlapping-handoff");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "probe-path.txt"),
                FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe"), timeout.Token);
            string ownerReceipt = Path.Combine(directory, "owner.json");
            string successorReceipt = Path.Combine(directory, "successor.json");
            Process owner = fixture.StartEditor("--verify-scheduler-overlap-owner", directory, ownerReceipt);
            await WaitReceipt(owner, ownerReceipt);
            using JsonDocument ready = await ProcessReceiptReader.ReadJsonAsync(ownerReceipt, timeout.Token);
            Assert.Equal("Ready", ready.RootElement.GetProperty("result").GetString());
            Assert.Equal(owner.Id, ready.RootElement.GetProperty("process_id").GetInt32());
            var old = new List<Process>();
            foreach (string key in new[] { "worker", "child", "grandchild" })
            {
                Process member = fixture.Own(ready.RootElement.GetProperty(key).GetInt32());
                _ = member.Handle;
                old.Add(member);
            }
            Process successor = fixture.StartEditor("--verify-scheduler-overlap-successor", directory, successorReceipt);
            await WaitReceipt(successor, successorReceipt + ".busy", successorReceipt + ".error");
            await File.WriteAllTextAsync(Path.Combine(directory, "release-owner"), "go", timeout.Token);
            await WaitReceipt(owner, ownerReceipt + ".paused", ownerReceipt + ".error");
            Assert.False(owner.HasExited);
            Assert.All(old, process => Assert.False(process.HasExited));
            await File.WriteAllTextAsync(Path.Combine(directory, "takeover"), "go", timeout.Token);
            await successor.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(successorReceipt + ".error"), File.Exists(successorReceipt + ".error")
                ? await File.ReadAllTextAsync(successorReceipt + ".error", timeout.Token) : "");
            Assert.Equal(0, successor.ExitCode);
            Assert.False(owner.HasExited);
            Assert.All(old, process => Assert.True(process.HasExited));
            await File.WriteAllTextAsync(Path.Combine(directory, "resume-old"), "go", timeout.Token);
            await owner.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(ownerReceipt + ".error"), File.Exists(ownerReceipt + ".error")
                ? await File.ReadAllTextAsync(ownerReceipt + ".error", timeout.Token) : "");
            Assert.Equal(0, owner.ExitCode);
            await Check(ownerReceipt, 6);
            await Check(successorReceipt, 7);
            output.WriteLine("live-old-worker-tree-at-takeover;held-process-handles=3;old-parent-resumed-after-successor-exit");

            async Task Check(string path, int expected)
            {
                using JsonDocument result = await ProcessReceiptReader.ReadJsonAsync(path, timeout.Token);
                Assert.Equal("Passed", result.RootElement.GetProperty("result").GetString());
                string[] checks = [.. result.RootElement.GetProperty("checks").EnumerateArray().Select(value => value.GetString()!)];
                Assert.Equal(expected, checks.Length);
                Assert.Equal(checks.Length, checks.Distinct().Count());
                output.WriteLine(string.Join(Environment.NewLine, checks));
            }

            async Task WaitReceipt(Process process, string path, string? errorPath = null)
            {
                errorPath ??= path + ".error";
                while (!File.Exists(path))
                {
                    Assert.False(File.Exists(errorPath), File.Exists(errorPath)
                        ? await File.ReadAllTextAsync(errorPath, timeout.Token) : "");
                    Assert.False(process.HasExited, "通知前に検証プロセスが終了しました。");
                    await Task.Delay(25, timeout.Token);
                }
            }
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
}
