using System.Diagnostics;
using System.Text;
using UWSCSharp.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerMonitorLeaseProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessOwnershipIsExclusiveAndEndsWithOwner(bool terminate)
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-monitor-process-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "schedule.monitor");
        Exception? primary = null;
        try
        {
            using SchedulerMonitorLease parent = Assert.IsType<SchedulerMonitorLease>(
                SchedulerMonitorLease.TryAcquire(path));
            const string script = """
                $ErrorActionPreference='Stop'
                $assembly=[Reflection.Assembly]::LoadFrom($env:UWSC_MONITOR_ASSEMBLY)
                $type=$assembly.GetType('UWSCSharp.Wpf.SchedulerMonitorLease',$true)
                $flags=[Reflection.BindingFlags]'Static,NonPublic'
                $acquire=$type.GetMethod('TryAcquire',$flags)
                $first=$acquire.Invoke($null,@($env:UWSC_MONITOR_PATH))
                if($null -ne $first){$first.Dispose();throw 'expected busy'}
                [Console]::WriteLine('busy')
                [Console]::Out.Flush()
                if([Console]::ReadLine() -ne 'acquire'){throw 'missing acquire'}
                $lease=$acquire.Invoke($null,@($env:UWSC_MONITOR_PATH))
                if($null -eq $lease){throw 'expected ownership'}
                try {
                    $property=$type.GetProperty('Generation',[Reflection.BindingFlags]'Instance,NonPublic')
                    [Console]::WriteLine($property.GetValue($lease))
                    [Console]::Out.Flush()
                    $null=[Console]::ReadLine()
                } finally { $lease.Dispose() }
                """;
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            start.Environment["UWSC_MONITOR_ASSEMBLY"] = typeof(SchedulerMonitorLease).Assembly.Location;
            start.Environment["UWSC_MONITOR_PATH"] = path;
            using Process process = Process.Start(start)!;
            Exception? processFailure = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
                Assert.Equal("busy", await process.StandardOutput.ReadLineAsync(timeout.Token));
                output.WriteLine($"busy: child={process.Id},exited={process.HasExited}");
                parent.Dispose();
                await process.StandardInput.WriteLineAsync("acquire");
                await process.StandardInput.FlushAsync(timeout.Token);
                string? generationText = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.True(Guid.TryParse(generationText, out Guid generation), generationText);
                Assert.NotEqual(parent.Generation, generation);
                output.WriteLine($"owned: child={process.Id},generation={generation},exited={process.HasExited}");
                AssertBusy(path);
                if (terminate)
                {
                    process.Kill(entireProcessTree: true);
                }
                else
                {
                    await process.StandardInput.WriteLineAsync("release");
                    await process.StandardInput.FlushAsync(timeout.Token);
                }

                await process.WaitForExitAsync(timeout.Token);
                string stderr = await error;
                output.WriteLine($"exited: child={process.Id},code={process.ExitCode},forced={terminate},stderr={stderr}");
                if (!terminate)
                {
                    Assert.True(process.ExitCode == 0, stderr);
                }

                using SchedulerMonitorLease next = Assert.IsType<SchedulerMonitorLease>(
                    SchedulerMonitorLease.TryAcquire(path));
                Assert.NotEqual(generation, next.Generation);
                Assert.True(File.Exists(path));
                output.WriteLine($"reacquired: generation={next.Generation}");
            }
            catch (Exception failure)
            {
                processFailure = failure;
                throw;
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                catch (Exception cleanup) when (processFailure is not null)
                {
                    throw new AggregateException(
                        "監視プロセスの検査とプロセス回収が失敗しました。",
                        processFailure, cleanup);
                }
            }
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            DeleteDirectory(directory, primary);
        }
    }

    [Fact]
    public void UnexpectedAcquisitionIsDisposedWhenBusyAssertionFails()
    {
        string directory = TemporaryDirectory();
        string path = Path.Combine(directory, "schedule.monitor");
        try
        {
            Assert.Throws<Xunit.Sdk.NullException>(() => AssertBusy(path));
            using SchedulerMonitorLease next = Assert.IsType<SchedulerMonitorLease>(
                SchedulerMonitorLease.TryAcquire(path));
            Assert.True(next.IsActive);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void FailedDeletionPreservesPrimaryFailureAndReportsSharingViolation()
    {
        string directory = TemporaryDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "schedule.monitor");
        var primary = new InvalidOperationException("検査の元の失敗");
        try
        {
            using var locked = new FileStream(path, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
            AggregateException error = Assert.Throws<AggregateException>(() =>
                DeleteDirectory(directory, primary));
            Assert.Equal(2, error.InnerExceptions.Count);
            Assert.Same(primary, error.InnerExceptions[0]);
            IOException cleanup = Assert.IsType<IOException>(error.InnerExceptions[1]);
            Assert.Equal(32, cleanup.HResult & 0xffff);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            DeleteDirectory(directory);
        }
        Assert.False(Directory.Exists(directory));
    }

    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(),
        "uwsc-monitor-process-" + Guid.NewGuid().ToString("N"));

    private void AssertBusy(string path)
    {
        // 予想外に取得できた場合も、アサートの失敗で所有ハンドルを失いません。
        using var unexpected = SchedulerMonitorLease.TryAcquire(path);
        output.WriteLine($"busy-check: acquired={unexpected is not null}");
        Assert.Null(unexpected);
    }

    private static void DeleteDirectory(string directory, Exception? primary = null)
    {
        try
        {
            string absolute = Path.GetFullPath(directory);
            string parent = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(absolute), parent,
                StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith(
                    "uwsc-monitor-process-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("監視プロセステストの回収先が範囲外です。");
            }

            if (Directory.Exists(absolute))
            {
                Directory.Delete(absolute, recursive: true);
            }
        }
        catch (Exception cleanup) when (primary is not null)
        {
            throw new AggregateException(
                "監視プロセスの検査と一時領域の回収が失敗しました。", primary, cleanup);
        }
    }
}
