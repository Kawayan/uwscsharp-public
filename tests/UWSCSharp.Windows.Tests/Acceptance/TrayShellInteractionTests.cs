using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class TrayShellInteractionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GuestMouseUsesExplorerIconAndDismissesOutsideTheMenu()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        string receipt = Path.Combine(fixture.DirectoryPath, "tray-shell.json");
        try
        {
            string directory = Path.Combine(fixture.DirectoryPath, "settings");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Process process = fixture.StartEditor("--verify-tray-shell", directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            string error = receipt + ".error";
            Assert.False(File.Exists(error), File.Exists(error) ? await File.ReadAllTextAsync(error) : "");
            Assert.Equal(0, process.ExitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
            Assert.Equal("Passed", document.RootElement.GetProperty("result").GetString());
            string[] checks = [.. document.RootElement.GetProperty("checks").EnumerateArray().Select(item => item.GetString()!)];
            Assert.Equal(4, checks.Length);
            Assert.Equal(4, checks.Distinct().Count());
            output.WriteLine(string.Join(Environment.NewLine, checks));
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            string events = receipt + ".events";
            if (File.Exists(events))
            {
                using var stream = new FileStream(events, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                output.WriteLine(await reader.ReadToEndAsync());
            }

            await fixture.CleanupAsync(primary);
        }
    }
}
