using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerControlDisconnectTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LivingWorkerStopsAfterOnlyControlPipeIsClosed()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string directory = Path.Combine(fixture.DirectoryPath, "control-disconnect");
            Directory.CreateDirectory(directory);
            string receipt = Path.Combine(directory, "result.json");
            Process process = fixture.StartEditor("--verify-scheduler-control-disconnect", directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(receipt + ".error"), File.Exists(receipt + ".error")
                ? await File.ReadAllTextAsync(receipt + ".error", timeout.Token) : "");
            Assert.Equal(0, process.ExitCode);
            using JsonDocument document = await ProcessReceiptReader.ReadJsonAsync(receipt, timeout.Token);
            Assert.Equal("Passed", document.RootElement.GetProperty("result").GetString());
            Assert.Equal(process.Id, document.RootElement.GetProperty("process_id").GetInt32());
            string[] checks = [.. document.RootElement.GetProperty("checks").EnumerateArray()
                .Select(value => value.GetString()!)];
            Assert.Equal(8, checks.Length);
            Assert.Equal(checks.Length, checks.Distinct().Count());
            Assert.Equal(1u, document.RootElement.GetProperty("exit_code").GetUInt32());
            output.WriteLine("control-only-disconnect;held-worker-handle=1");
            output.WriteLine("disconnect-to-exit-ms=" + document.RootElement.GetProperty("disconnect_to_exit_ms").GetInt64());
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
