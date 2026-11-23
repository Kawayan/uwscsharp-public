using System.Diagnostics;

namespace UWSCSharp.Windows.Tests;

internal sealed class AssociationLaunchFixture(
    Action<string>? log = null,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, Task>? delay = null)
{
    private readonly List<Process> processes = [];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, Task> wait = delay ?? Task.Delay;

    internal string DirectoryPath
    {
        get;
    } = Path.Combine(Path.GetTempPath(),
        "uwsc-association-" + Guid.NewGuid());

    internal string AppDirectory => Path.Combine(DirectoryPath, "app");

    internal string Executable => Path.Combine(AppDirectory, "UWSCSharp.Association.exe");

    internal static AssociationLaunchFixture Create(
        Action<string>? log = null,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, Task>? delay = null)
    {
        var fixture = new AssociationLaunchFixture(log, timeProvider, delay);
        try
        {
            Directory.CreateDirectory(fixture.AppDirectory);
            string original = FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(original)!))
            {
                string copied = Path.Combine(fixture.AppDirectory, Path.GetFileName(file));
                File.Copy(file, copied);
                File.SetAttributes(copied, FileAttributes.Normal);
            }

            File.Copy(original, fixture.Executable);
            return fixture;
        }
        catch (Exception primary)
        {
            try
            {
                if (Directory.Exists(fixture.DirectoryPath))
                {
                    Directory.Delete(fixture.DirectoryPath, recursive: true);
                }
            }
            catch (Exception cleanup)
            {
                primary.Data["検査準備の回収エラー"] = cleanup;
            }

            throw;
        }
    }

    internal Process Start(params string[] arguments) =>
        StartExecutable(Executable, arguments);

    internal Process StartEditor(params string[] arguments) =>
        StartExecutable(Path.Combine(AppDirectory, "UWSCSharp.Wpf.exe"), arguments);

    internal Process OpenAssociatedScript(string path) =>
        StartProcess(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "open",
            WorkingDirectory = AppDirectory
        });

    internal Process Own(int processId)
    {
        var process = Process.GetProcessById(processId);
        processes.Add(process);
        return process;
    }

    internal Process StartExecutable(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = AppDirectory
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return StartProcess(start);
    }

    private Process StartProcess(ProcessStartInfo start)
    {
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("関連付けプロセスを開始できませんでした。");
            }

            processes.Add(process);
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal async Task CleanupAsync(Exception? primary)
    {
        var errors = new List<Exception>();
        foreach (Process process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                    log?.Invoke($"cleanup-process={process.Id},forced=True,exited={process.HasExited}");
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            await DeleteDirectoryAsync();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        if (errors.Count > 0)
        {
            var cleanup = new AggregateException(errors);
            if (primary is not null)
            {
                primary.Data["検査後の回収エラー"] = cleanup;
            }
            else
            {
                throw cleanup;
            }
        }
    }

    private async Task DeleteDirectoryAsync()
    {
        long started = clock.GetTimestamp();
        int failures = 0;
        while (true)
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
                log?.Invoke($"cleanup-delete: failures={failures},elapsed-ms={(long)clock.GetElapsedTime(started).TotalMilliseconds}");
                return;
            }
            catch (Exception error) when (clock.GetElapsedTime(started) < TimeSpan.FromSeconds(5)
                && (error is UnauthorizedAccessException
                    || error is IOException io && (io.HResult & 0xffff) is 32 or 33))
            {
                failures++;
                if (failures == 1)
                {
                    log?.Invoke($"cleanup-delete-retry: {error.GetType().Name},hresult=0x{error.HResult:x8}");
                    string core = Path.Combine(AppDirectory, "UWSCSharp.Core.dll");
                    if (File.Exists(core))
                    {
                        log?.Invoke($"cleanup-core-attributes={File.GetAttributes(core)}");
                    }
                }

                // 終了直後の削除拒否だけを期限内で再試行し、削除完了を必須にする。
                await wait(TimeSpan.FromMilliseconds(50));
            }
        }
    }
}
