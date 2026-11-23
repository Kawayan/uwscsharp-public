using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ApplicationIniConcurrentSaveTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ConcurrentProcessesWriteConsistentSettings(
        bool exists, bool sameKey)
    {
        string directory = Directory.CreateTempSubdirectory(
            "uwsc-ini-concurrent-").FullName;
        string ini = Path.Combine(directory, "UWSC.INI");
        string release = Path.Combine(directory, "release");
        string firstReady = Path.Combine(directory, "first-ready");
        string logReady = Path.Combine(directory, "log-ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        CancellationToken token = timeout.Token;
        string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        Process? first = null;
        Process? log = null;
        try
        {
            string original = "";
            if (exists)
            {
                original = ";" + new string('x', 4 * 1024 * 1024)
                    + "\r\n[SET]\r\nKeep=unknown\r\n";
                await File.WriteAllTextAsync(ini, original, token);
            }

            string firstMode = sameKey ? "log-alternate" : "window";
            first = Start(fixture, firstMode, ini, firstReady, release);
            log = Start(fixture, "log", ini, logReady, release);
            Task<string> firstErrors = first.StandardError.ReadToEndAsync(token);
            Task<string> logErrors = log.StandardError.ReadToEndAsync(token);
            while (!File.Exists(firstReady) || !File.Exists(logReady))
            {
                Assert.False(first.HasExited || log.HasExited);
                await Task.Delay(15, token);
            }

            await File.WriteAllTextAsync(release, "", token);
            await Task.WhenAll(first.WaitForExitAsync(token),
                log.WaitForExitAsync(token));
            Assert.True(first.ExitCode == 0, await firstErrors);
            Assert.True(log.ExitCode == 0, await logErrors);

            ApplicationWindowSettings windowSettings =
                await ApplicationWindowSettings.LoadAsync(ini, token);
            ScriptLogDefaults logSettings =
                await ScriptLogDefaults.LoadAsync(ini, token);
            string content = await File.ReadAllTextAsync(ini, token);
            if (sameKey)
            {
                Assert.Contains(logSettings.MaximumLines, new[] { 123, 456 });
                string line = Assert.Single(content.Split("\r\n"),
                    line => line.StartsWith("LogLines=", StringComparison.Ordinal));
                Assert.Equal($"LogLines={logSettings.MaximumLines}", line);
            }
            else
            {
                Assert.True(windowSettings.StayOnTop);
                Assert.Equal(123, logSettings.MaximumLines);
            }

            if (exists)
            {
                Assert.True(content.StartsWith(original, StringComparison.Ordinal),
                    "既存コメントと未知の設定が保持されていません。");
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(StopAsync(first), StopAsync(log));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryJunctionSharesTheTargetIniMutex(bool exists)
    {
        string directory = Directory.CreateTempSubdirectory(
            "uwsc-ini-alias-").FullName;
        string target = Path.Combine(directory, "physical");
        string alias = Path.Combine(directory, "alias");
        Directory.CreateDirectory(target);
        string ini = Path.Combine(target, "UWSC.INI");
        string release = Path.Combine(directory, "release");
        string ready = Path.Combine(directory, "ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = timeout.Token;
        Process? child = null;
        try
        {
            await CreateJunctionAsync(alias, target, token);
            if (exists)
            {
                await File.WriteAllTextAsync(ini,
                    "[SET]\r\nKeep=unknown\r\n", token);
            }

            string fixture = FixturePaths.Get("UWSCSharp.ControlFixture",
                "UWSCSharp.ControlFixture.exe");
            child = Start(fixture, "window", Path.Combine(alias, "UWSC.INI"),
                ready, release);
            Task<string> errors = child.StandardError.ReadToEndAsync(token);
            while (!File.Exists(ready))
            {
                Assert.False(child.HasExited);
                await Task.Delay(15, token);
            }

            byte[] path = Encoding.UTF8.GetBytes(ini.ToUpperInvariant());
            string name = "UWSCSharp.Ini." + Convert.ToHexString(SHA256.HashData(path));
            using var mutex = new Mutex(false, name, new NamedWaitHandleOptions
            {
                CurrentUserOnly = true,
                CurrentSessionOnly = false
            });
            Assert.True(mutex.WaitOne(TimeSpan.FromSeconds(3)));
            try
            {
                File.WriteAllText(release, "");
                Assert.False(child.WaitForExit(1500),
                    "ジャンクション経由の保存が実体側のミューテックスを待たずに終了しました。");
            }
            finally
            {
                mutex.ReleaseMutex();
            }

            await child.WaitForExitAsync(token);
            Assert.True(child.ExitCode == 0, await errors);
            ApplicationWindowSettings saved =
                await ApplicationWindowSettings.LoadAsync(ini, token);
            Assert.True(saved.StayOnTop);
            if (exists)
            {
                Assert.Contains("Keep=unknown", await File.ReadAllTextAsync(ini, token),
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            try
            {
                await StopAsync(child);
            }
            finally
            {
                if (Directory.Exists(alias))
                {
                    Directory.Delete(alias);
                }

                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task CreateJunctionAsync(string alias, string target,
        CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "cmd.exe"))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/j", alias, target })
        {
            start.ArgumentList.Add(argument);
        }

        using Process child = Process.Start(start)!;
        Task<string> errors = child.StandardError.ReadToEndAsync(token);
        await child.WaitForExitAsync(token);
        Assert.True(child.ExitCode == 0, await errors);
        Assert.True(File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint));
    }

    private static Process Start(string fixture, string mode, string ini,
        string ready, string release)
    {
        var start = new ProcessStartInfo(fixture)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "--ini-settings-save", mode, ini, ready, release })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }

    private static async Task StopAsync(Process? process)
    {
        using (process)
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
