using System.Diagnostics;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class WpfRestartCancellationAcceptanceTests
{
    [Theory]
    [InlineData("--verify-restart-cancellation")]
    [InlineData("--verify-restart-confirmation-message")]
    [InlineData("--verify-power-commands")]
    public async Task PowerCommandsFollowTheEditorPlaybackRoute(string command)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create();
        Exception? primary = null;
        try
        {
            string receipt = Path.Combine(fixture.DirectoryPath, "restart.txt");
            Process process = fixture.StartEditor(command,
                fixture.DirectoryPath, receipt);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            string diagnostic = File.Exists(receipt + ".error")
                ? await File.ReadAllTextAsync(receipt + ".error") : string.Empty;
            Assert.True(process.ExitCode == 0, diagnostic);
            Assert.Equal("Passed", await File.ReadAllTextAsync(receipt));
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
