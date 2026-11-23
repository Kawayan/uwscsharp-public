using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerHotkeyRestoreTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ActualParentHotkeyConflictPreservesFailureAndUnrecoveredSlot()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        string? conditions = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string directory = Path.Combine(fixture.DirectoryPath, "hotkey-restore");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "probe-path.txt"),
                FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe"), timeout.Token);
            string receipt = Path.Combine(directory, "result.json");
            conditions = receipt + ".conditions";
            Process process = fixture.StartEditor("--verify-scheduler-hotkey-restore", directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(receipt + ".error"), File.Exists(receipt + ".error")
                ? await File.ReadAllTextAsync(receipt + ".error", timeout.Token) : "");
            Assert.Equal(0, process.ExitCode);
            using JsonDocument document = await ProcessReceiptReader.ReadJsonAsync(receipt, timeout.Token);
            Assert.Equal("Passed", document.RootElement.GetProperty("result").GetString());
            Assert.Equal(process.Id, document.RootElement.GetProperty("process_id").GetInt32());
            string[] checks = [.. document.RootElement.GetProperty("checks").EnumerateArray()
                .Select(value => value.GetString()!)];
            Assert.Equal(10, checks.Length);
            Assert.Equal(checks.Length, checks.Distinct().Count());
            output.WriteLine("actual-native-hotkey-restore-conflict;owned-editor-f11-down-up");
            output.WriteLine(string.Join(Environment.NewLine, checks));
        }
        catch (Exception error)
        {
            primary = error;
            if (conditions is not null && File.Exists(conditions))
            {
                output.WriteLine(await File.ReadAllTextAsync(conditions));
            }
            throw;
        }
        finally
        {
            await fixture.CleanupAsync(primary);
        }
    }
}
