using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerOwnershipLossTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LiveOwnerLosesActualLeaseCancelsWorkerAndSuccessorKeepsConsumedOccurrence()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            string directory = Path.Combine(fixture.DirectoryPath, "ownership-loss");
            Directory.CreateDirectory(directory);
            string ownerReceipt = Path.Combine(directory, "owner.json");
            string successorReceipt = Path.Combine(directory, "successor.json");
            Process owner = fixture.StartEditor("--verify-scheduler-ownership-owner", directory, ownerReceipt);
            await WaitReceipt(owner, ownerReceipt);
            using JsonDocument ready = await ProcessReceiptReader.ReadJsonAsync(ownerReceipt, timeout.Token);
            Assert.Equal("Ready", ready.RootElement.GetProperty("result").GetString());
            Assert.Equal(owner.Id, ready.RootElement.GetProperty("process_id").GetInt32());
            Process worker = fixture.Own(ready.RootElement.GetProperty("worker_pid").GetInt32());
            _ = worker.Handle;
            Assert.False(worker.HasExited);
            Process successor = fixture.StartEditor("--verify-scheduler-ownership-successor", directory, successorReceipt);
            await WaitReceipt(successor, successorReceipt + ".busy", successorReceipt + ".error");
            Assert.False(owner.HasExited);
            await File.WriteAllTextAsync(Path.Combine(directory, "lose-lease"), "go", timeout.Token);
            await WaitReceipt(owner, ownerReceipt + ".lost", ownerReceipt + ".error");
            Assert.False(owner.HasExited);
            Assert.True(worker.HasExited);
            await File.WriteAllTextAsync(Path.Combine(directory, "takeover"), "go", timeout.Token);
            await successor.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(successorReceipt + ".error"), File.Exists(successorReceipt + ".error")
                ? await File.ReadAllTextAsync(successorReceipt + ".error", timeout.Token) : "");
            Assert.Equal(0, successor.ExitCode);
            Assert.False(owner.HasExited);
            await File.WriteAllTextAsync(Path.Combine(directory, "finish"), "go", timeout.Token);
            await owner.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(ownerReceipt + ".error"), File.Exists(ownerReceipt + ".error")
                ? await File.ReadAllTextAsync(ownerReceipt + ".error", timeout.Token) : "");
            Assert.Equal(0, owner.ExitCode);
            await Check(ownerReceipt, 7);
            await Check(successorReceipt, 6);
            output.WriteLine("actual-lease-close;old-owner-alive-through-successor-completion;held-worker-handle=1");

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
