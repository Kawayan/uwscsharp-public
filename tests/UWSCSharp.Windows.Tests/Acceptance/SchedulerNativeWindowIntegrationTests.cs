using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerNativeWindowIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NativeCaptureFeedsBothModesAndStopWaitsForSingleObserver()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string receipt = Path.Combine(fixture.DirectoryPath, "window.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            System.Diagnostics.Process process = fixture.StartEditor("--verify-scheduler-window",
                Path.Combine(fixture.DirectoryPath, "windows"), receipt);
            await process.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(receipt + ".error"), File.Exists(receipt + ".error")
                ? await File.ReadAllTextAsync(receipt + ".error", timeout.Token) : "");
            Assert.Equal(0, process.ExitCode);
            using JsonDocument document = await ProcessReceiptReader.ReadJsonAsync(receipt, timeout.Token);
            Assert.Equal("Passed", document.RootElement.GetProperty("result").GetString());
            Assert.Equal(process.Id, document.RootElement.GetProperty("process_id").GetInt32());
            string[] checks = [.. document.RootElement.GetProperty("checks").EnumerateArray()
                .Select(item => item.GetString()!)];
            Assert.Equal(15, checks.Length);
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
