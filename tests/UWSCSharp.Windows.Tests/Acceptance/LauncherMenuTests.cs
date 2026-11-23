using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class LauncherMenuTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LauncherRegistersWithWindowsAndReleasesOnExit()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string directory = Path.Combine(fixture.DirectoryPath, "settings");
            string receipt = Path.Combine(fixture.DirectoryPath, "hotkey.txt");
            Assert.Equal(0, ProbeShortcut());
            Assert.Equal(0, ProbeShortcut(131));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string targetPath = FixturePaths.Get("UWSCSharp.ControlFixture",
                "UWSCSharp.ControlFixture.exe");
            Process target = fixture.StartExecutable(targetPath, []);
            while (!target.HasExited && target.MainWindowHandle == 0)
            {
                await Task.Delay(50, timeout.Token);
                target.Refresh();
            }

            Assert.False(target.HasExited);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "target.pid"),
                target.Id.ToString(CultureInfo.InvariantCulture));
            Process process = fixture.StartEditor(
                "--verify-launcher-hotkey-registration", directory, receipt);
            while (!File.Exists(receipt + ".ready") && !process.HasExited)
            {
                await Task.Delay(50, timeout.Token);
            }

            Assert.False(process.HasExited, ReadError(receipt));
            Assert.Equal(1409, ProbeShortcut());
            Assert.Equal(0, ProbeShortcut(131));
            File.WriteAllText(receipt + ".close", "");
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, ReadError(receipt));
            Assert.Equal("Passed", await File.ReadAllTextAsync(receipt));
            Assert.Equal(0, ProbeShortcut());
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

    private static string ReadError(string receipt) => File.Exists(receipt + ".error")
        ? File.ReadAllText(receipt + ".error") : "ランチ診断が終了しました。";

    private static int ProbeShortcut(uint key = 130)
    {
        const int id = 1;
        if (!RegisterHotKey(0, id, 6, key))
        {
            return Marshal.GetLastWin32Error();
        }

        Assert.True(UnregisterHotKey(0, id));
        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id,
        uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [Fact]
    public async Task MenuAndDialogsKeepRegistrationAndEditorStateAcrossFailures()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string directory = Path.Combine(fixture.DirectoryPath, "settings");
            string receipt = Path.Combine(fixture.DirectoryPath, "menu.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Process process = fixture.StartEditor("--verify-launcher-menu", directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            string error = receipt + ".error";
            Assert.False(File.Exists(error), File.Exists(error) ? await File.ReadAllTextAsync(error) : "");
            Assert.Equal(0, process.ExitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
            Assert.Equal("Passed", document.RootElement.GetProperty("result").GetString());
            string[] checks = [.. document.RootElement.GetProperty("checks").EnumerateArray().Select(item => item.GetString()!)];
            Assert.Equal(24, checks.Length);
            Assert.Equal(24, checks.Distinct().Count());
            Assert.Contains("offscreen-position-keeps-menu-on-monitor", checks);
            Assert.Contains("settings-conflict-keeps-newer-file-and-active-hotkey",
                checks);
            output.WriteLine("dpi=" + document.RootElement.GetProperty("dpi").GetDouble());
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
