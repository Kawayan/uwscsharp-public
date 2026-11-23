using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerWorkerRuntimeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ActualWorkersUseOwnedProcessesAndKeepExecutionAndRecoverySeparate()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string directory = Path.Combine(fixture.DirectoryPath, "worker");
            string receipt = Path.Combine(fixture.DirectoryPath, "worker.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(160));
            Process process = fixture.StartEditor("--verify-scheduler-worker", directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(receipt + ".error"), File.Exists(receipt + ".error")
                ? await File.ReadAllTextAsync(receipt + ".error", timeout.Token) : "");
            Assert.Equal(0, process.ExitCode);
            using JsonDocument document = await ProcessReceiptReader.ReadJsonAsync(receipt, timeout.Token);
            JsonElement root = document.RootElement;
            Assert.Equal("Passed", root.GetProperty("result").GetString());
            Assert.Equal(process.Id, root.GetProperty("process_id").GetInt32());
            Assert.Equal(11, root.GetProperty("completed").GetInt32());
            string[] checks = [.. root.GetProperty("checks").EnumerateArray().Select(item => item.GetString()!)];
            Assert.Equal(37, checks.Length);
            Assert.Contains(checks, check => check.StartsWith(
                "worker-font-ini-and-override:", StringComparison.Ordinal));
            Assert.Contains("worker-power-and-restart-rejection", checks);
            Assert.Equal(checks.Length, checks.Distinct().Count());
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
}
