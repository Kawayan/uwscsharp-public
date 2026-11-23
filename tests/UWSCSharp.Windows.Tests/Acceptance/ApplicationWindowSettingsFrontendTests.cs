using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class ApplicationWindowSettingsFrontendTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("interactive", false)]
    [InlineData("interactive", true)]
    [InlineData("script", false)]
    [InlineData("script", true)]
    [InlineData("association", false)]
    [InlineData("association", true)]
    public async Task NormalStartupAppliesStayOnTopBeforeShowingTheMainWindow(
        string mode, bool enabled)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        string profile = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "UWSC", "UWSC.INI");
        byte[]? previous = File.Exists(profile) ? await File.ReadAllBytesAsync(profile) : null;
        Exception? primary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            await File.WriteAllTextAsync(profile, "[SET]\nStayOnTop=" + (enabled ? 1 : 0)
                + "\nMainSize=" + (enabled ? "1000,700" : "440,122"));
            string script = Path.Combine(fixture.DirectoryPath, "startup.uws");
            string release = Path.Combine(fixture.DirectoryPath, "release.go");
            string source = $$"""
                FOR index=1 TO 2000
                    IF FOPEN("{{release}}",F_EXISTS) THEN BREAK
                    SLEEP(0.01)
                NEXT
                PRINT "completed"
                """;
            await File.WriteAllTextAsync(script, source, new UTF8Encoding(true));
            Process process = mode == "association" ? fixture.Start(script)
                : mode == "script" ? fixture.StartEditor(script) : fixture.StartEditor();
            nint main = await FindMainAsync(process);
            int style = Native.GetWindowLongW(main, -20);
            output.WriteLine("window-settings=" + JsonSerializer.Serialize(new
            {
                mode,
                enabled,
                topmost = (style & 8) != 0,
                visible = Native.IsWindowVisible(main),
                pid = process.Id
            }));
            Assert.Equal(enabled, (style & 8) != 0);
            using (var physical = new PhysicalDpiScope())
            {
                Assert.True(Native.GetWindowRect(main, out Native.Rect bounds));
                output.WriteLine($"main-size-physical={bounds.Right - bounds.Left},{bounds.Bottom - bounds.Top}");
                if (enabled)
                {
                    Assert.Equal(1000, bounds.Right - bounds.Left);
                    Assert.True(bounds.Bottom - bounds.Top >= 700);
                }
                else
                {
                    Assert.True(bounds.Right - bounds.Left >= 620);
                    Assert.True(bounds.Bottom - bounds.Top > 122);
                }
            }
            if (mode == "interactive")
            {
                Assert.True(process.CloseMainWindow());
            }
            else
            {
                await File.WriteAllTextAsync(release, "release");
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                await fixture.CleanupAsync(primary);
            }
            finally
            {
                if (previous is null)
                {
                    if (File.Exists(profile))
                    {
                        File.Delete(profile);
                    }
                }
                else
                {
                    await File.WriteAllBytesAsync(profile, previous);
                }
            }
        }
    }

    private static async Task<nint> FindMainAsync(Process process)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            Assert.False(process.HasExited);
            nint found = 0;
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out uint owner);
                if (owner == process.Id && Native.IsWindowVisible(window))
                {
                    var title = new StringBuilder(128);
                    Native.GetWindowTextW(window, title, title.Capacity);
                    if (title.ToString() == "UWSC#（記録・一時停止 試作版）")
                    {
                        found = window;
                        return false;
                    }
                }

                return true;
            }, 0);
            if (found != 0)
            {
                return found;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("本体の初回表示を確認できませんでした。");
    }
}
