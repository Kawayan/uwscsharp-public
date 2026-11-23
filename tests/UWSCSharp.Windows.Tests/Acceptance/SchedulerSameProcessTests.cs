using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerSameProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task IndependentExecutionsKeepEditorAndShareInteractiveGate()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string directory = Path.Combine(fixture.DirectoryPath, "scheduler");
            string receipt = Path.Combine(fixture.DirectoryPath, "scheduler.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Process process = fixture.StartEditor(
                "--verify-scheduler-same-process", directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            string error = receipt + ".error";
            Assert.False(File.Exists(error), File.Exists(error)
                ? await File.ReadAllTextAsync(error, timeout.Token) : "");
            Assert.Equal(0, process.ExitCode);
            using JsonDocument document = await ProcessReceiptReader.ReadJsonAsync(
                receipt, timeout.Token);
            JsonElement root = document.RootElement;
            Assert.Equal("Passed", root.GetProperty("result").GetString());
            Assert.Equal(process.Id, root.GetProperty("process_id").GetInt32());
            Assert.Equal(7, root.GetProperty("completed").GetInt32());
            string[] checks = [.. root.GetProperty("checks").EnumerateArray()
                .Select(item => item.GetString()!)];
            Assert.Equal(42, checks.Length);
            Assert.Contains("scheduled-system-power-routing", checks);
            Assert.Contains("transferred-run-blocked-without-discard-during-scheduled-playback", checks);
            Assert.Equal(checks.Length, checks.Distinct().Count());
            output.WriteLine(string.Join(Environment.NewLine, checks));
            JsonElement fonts = root.GetProperty("fonts");
            Assert.Equal(7, fonts.GetArrayLength());
            foreach (JsonElement font in fonts.EnumerateArray())
            {
                Assert.Equal(process.Id, font.GetProperty("ProcessId").GetInt32());
                Assert.True(font.GetProperty("Dpi").GetUInt32() > 0);
            }

            output.WriteLine(fonts.GetRawText());
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
