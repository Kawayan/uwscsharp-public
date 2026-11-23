using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class TrayLauncherTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NativeTrayCallbacksDelaySingleClickAndPreserveDoubleClickRestore()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string directory = Path.Combine(fixture.DirectoryPath, "settings");
            string receipt = Path.Combine(fixture.DirectoryPath, "menu.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Process process = fixture.StartEditor("--verify-tray-launcher", directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            string error = receipt + ".error";
            Assert.False(File.Exists(error), File.Exists(error) ? await File.ReadAllTextAsync(error) : "");
            Assert.Equal(0, process.ExitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
            Assert.Equal("Passed", document.RootElement.GetProperty("result").GetString());
            string[] checks = [.. document.RootElement.GetProperty("checks").EnumerateArray().Select(item => item.GetString()!)];
            Assert.Equal(6, checks.Length);
            Assert.Equal(6, checks.Distinct().Count());
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
