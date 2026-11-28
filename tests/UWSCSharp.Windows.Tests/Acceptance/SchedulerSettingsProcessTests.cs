using System.Diagnostics;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerSettingsProcessTests
{
    [Fact]
    public async Task AnotherProcessUsesSameStoreAndInvalidatesStaleRevision()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-scheduler-process-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "schedule.json");
        try
        {
            SchedulerSettingsSnapshot first = SchedulerSettingsStore.Save(path,
                new SchedulerSettings(), null);
            string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--scheduler-settings-probe");
            start.ArgumentList.Add(path);
            using Process process = Process.Start(start)!;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await error);
                var revision = Guid.Parse((await output).Trim());
                Assert.NotEqual(first.Revision, revision);
                byte[] changed = await File.ReadAllBytesAsync(path, timeout.Token);
                Assert.Throws<SchedulerSettingsConflictException>(() =>
                    SchedulerSettingsStore.Save(path, first.Settings, first.Revision));
                Assert.Equal(changed, await File.ReadAllBytesAsync(path, timeout.Token));
                Assert.Equal(revision, SchedulerSettingsStore.Load(path).Revision);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            string absolute = Path.GetFullPath(directory);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(absolute), parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("uwsc-scheduler-process-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("予約設定のプロセステスト回収先が範囲外です。");
            }

            if (Directory.Exists(absolute))
            {
                Directory.Delete(absolute, recursive: true);
            }
        }
    }
}
