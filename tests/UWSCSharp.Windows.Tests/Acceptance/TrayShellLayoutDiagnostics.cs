using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class TrayShellLayoutDiagnostics(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserveExplorerElementsWithoutMouseInput(bool restartExplorer)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        string receipt = Path.Combine(fixture.DirectoryPath, "tray-shell.json");
        try
        {
            string directory = Path.Combine(fixture.DirectoryPath, "settings");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string mode = restartExplorer ? "--inspect-tray-explorer-restart"
                : "--inspect-tray-layout";
            Process process = fixture.StartEditor(mode, directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            string error = receipt + ".error";
            Assert.False(File.Exists(error), File.Exists(error) ? await File.ReadAllTextAsync(error) : "");
            Assert.Equal(0, process.ExitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
            Assert.Equal("Observed", document.RootElement.GetProperty("result").GetString());
            if (restartExplorer)
            {
                Assert.True(document.RootElement.GetProperty("restartedShell").GetInt32() > 0);
            }

            output.WriteLine(document.RootElement.GetRawText());
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
