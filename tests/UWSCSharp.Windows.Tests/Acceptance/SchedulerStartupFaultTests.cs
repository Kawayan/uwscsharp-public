using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerStartupFaultTests(ITestOutputHelper output)
{
    [Fact]
    public async Task UnconnectedChildExitCancellationAndDeadlineDrainActualProcesses()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string directory = Path.Combine(fixture.DirectoryPath, "startup-fault");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "probe-path.txt"),
                FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe"), timeout.Token);
            string receipt = Path.Combine(directory, "result.json");
            Process process = fixture.StartEditor("--verify-scheduler-startup-fault", directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(receipt + ".error"), File.Exists(receipt + ".error")
                ? await File.ReadAllTextAsync(receipt + ".error", timeout.Token) : "");
            Assert.Equal(0, process.ExitCode);
            using JsonDocument document = await ProcessReceiptReader.ReadJsonAsync(receipt, timeout.Token);
            Assert.Equal("Passed", document.RootElement.GetProperty("result").GetString());
            Assert.Equal(process.Id, document.RootElement.GetProperty("process_id").GetInt32());
            JsonElement cases = document.RootElement.GetProperty("cases");
            Assert.Equal(3, cases.GetArrayLength());
            string?[] expected = ["exit", "cancel", "timeout"];
            Assert.Equal(expected,
                cases.EnumerateArray().Select(value => value.GetProperty("mode").GetString()));
            output.WriteLine(cases.GetRawText());
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
