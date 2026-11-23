using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class AuthoringWindowRuntimeTests(ITestOutputHelper output)
{
    private const string Title = "UWSC# Authoring Fixture";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WpfRunsOriginalWindowScriptAgainstOwnedFixture(bool present)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        Assert.Empty(MatchingWindows());
        await using PumpedControlFixture? target = present
            ? await PumpedControlFixture.CreateAsync(_ => { }, title: Title)
            : null;
        await using PumpedControlFixture cover =
            await PumpedControlFixture.CreateAsync(_ => { },
                title: "UWSC# Authoring Baseline " + Guid.NewGuid().ToString("N"));
        nint expected = target?.Root ?? 0;
        nint[] expectedWindows = present ? [expected] : [];
        Assert.Equal(expectedWindows, MatchingWindows());
        if (present)
        {
            Native.GetWindowThreadProcessId(expected, out uint owner);
            Assert.Equal((uint)Environment.ProcessId, owner);
            Assert.True(Native.IsWindowVisible(expected));
        }

        await UiInputDriver.ActivateAsync(cover.Root);
        Assert.Equal(cover.Root, Native.GetForegroundWindow());
        string executable = FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
        string script = Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Authoring", "find-and-activate-fixture-window.uws");
        Assert.True(File.Exists(executable), executable);
        Assert.True(File.Exists(script), script);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(script);
        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool sawTargetForeground = false;
        var elapsed = Stopwatch.StartNew();
        try
        {
            while (!process.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(30))
            {
                sawTargetForeground |= present
                    && Native.GetForegroundWindow() == expected;
                await Task.Delay(10);
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(12));
                throw new TimeoutException("WPF実行が制限時間内に終了しませんでした。");
            }

            sawTargetForeground |= present
                && Native.GetForegroundWindow() == expected;
            string actual = await stdout;
            string errors = await stderr;
            output.WriteLine("AUTHORING_WINDOW_RECEIPT " + JsonSerializer.Serialize(new
            {
                present,
                target = (long)expected,
                cover = (long)cover.Root,
                processId = process.Id,
                scriptSha256 = Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(script))),
                executableSha256 = Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(executable))),
                foreground = (long)Native.GetForegroundWindow(),
                sawTargetForeground,
                exitCode = process.ExitCode,
                stdout = actual,
                stderr = errors,
                elapsedMs = elapsed.ElapsedMilliseconds
            }));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", errors);
            Assert.Equal(present ? "" : "未発見", actual.TrimEnd('\r', '\n'));
            if (present)
            {
                Assert.True(sawTargetForeground,
                    "専用ウィンドウの前面化を観測できませんでした。");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(12));
            }
        }
    }

    private static nint[] MatchingWindows()
    {
        List<nint> windows = [];
        Native.EnumWindows((window, _) =>
        {
            var text = new StringBuilder(512);
            Native.GetWindowTextW(window, text, text.Capacity);
            if (text.ToString().Contains(Title, StringComparison.OrdinalIgnoreCase))
            {
                windows.Add(window);
            }

            return true;
        }, 0);
        return [.. windows];
    }
}
