using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// 予約1実行のプロセスと子孫Jobを保持します。
/// </summary>
internal sealed class SchedulerChildProcess : IDisposable
{
    private const int ExitPollMilliseconds = 25;

    private readonly object sync = new();
    private readonly SafeFileHandle process;
    private readonly SafeFileHandle job;
    private readonly Action captureGroup;
    private readonly SchedulerJobDrain drain;
    private readonly CancellationTokenSource lifetime = new();
    private Task<uint>? exit;
    private bool disposed;

    private SchedulerChildProcess(SafeFileHandle process, SafeFileHandle job,
        uint id, Action captureGroup)
    {
        this.process = process;
        this.job = job;
        Id = id;
        this.captureGroup = captureGroup;
        drain = new SchedulerJobDrain(job);
    }

    internal uint Id
    {
        get;
    }

    internal static SchedulerChildProcess Start(string executable,
        IReadOnlyList<string> arguments, string workingDirectory,
        SafeFileHandle monitorJob, Action captureGroup, CancellationToken token,
        SchedulerProcessCreation? creation = null)
    {
        if (!Path.IsPathFullyQualified(executable)
            || !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("予約の実行ファイルと作業フォルダーは絶対パスが必要です。");
        }
        var command = new StringBuilder(WindowsPowerShellCommand.Quote(executable));
        foreach (string argument in arguments)
        {
            if (argument.Contains('\0'))
            {
                throw new ArgumentException("予約の引数にNULは使えません。", nameof(arguments));
            }
            command.Append(' ').Append(WindowsPowerShellCommand.Quote(argument));
        }
        if (command.Length >= 32767 || executable.Contains('\0')
            || workingDirectory.Contains('\0'))
        {
            throw new ArgumentException("予約の起動文字列が長すぎるかNULを含みます。");
        }
        token.ThrowIfCancellationRequested();
        SafeFileHandle job = SchedulerProcessGroup.CreateJobObjectW(0, null);
        if (job.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw error;
        }
        try
        {
            OwnedProcessJob.EnableKillOnClose(job);
            var security = new CapturedProcess.SecurityAttributes
            {
                Size = Marshal.SizeOf<CapturedProcess.SecurityAttributes>(),
                Inherit = true
            };
            using SafeFileHandle sink = CapturedProcess.CreateFileW("NUL",
                0xc0000000, 3, ref security, 3, 0, 0);
            if (sink.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            using var attributes = new SchedulerProcessAttributes(sink, monitorJob, job);
            var startup = new CapturedProcess.StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<CapturedProcess.StartupInfoEx>(),
                    Flags = 0x100,
                    Input = sink.DangerousGetHandle(),
                    Output = sink.DangerousGetHandle(),
                    Error = sink.DangerousGetHandle()
                },
                Attributes = attributes.Pointer
            };
            token.ThrowIfCancellationRequested();
            // 作成時に両Jobを指定し、所属前の停止中プロセスを作りません。
            if (!CapturedProcess.CreateProcessW(executable, command,
                0, 0, true, 0x08080000, 0, workingDirectory,
                ref startup, out CapturedProcess.ProcessInfo information))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            creation?.MarkCreated(information.ProcessId);
            var process = new SafeFileHandle(information.Process, true);
            using var thread = new SafeFileHandle(information.Thread, true);
            var child = new SchedulerChildProcess(process, job,
                information.ProcessId, captureGroup);
            try
            {
                captureGroup();
                child.drain.Capture();
                return child;
            }
            catch
            {
                child.Dispose();
                throw;
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    internal Task<uint> WaitForExitAsync(CancellationToken token = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            exit ??= WaitForExitCoreAsync();
            return exit.WaitAsync(token);
        }
    }

    private async Task<uint> WaitForExitCoreAsync()
    {
        CancellationToken token = lifetime.Token;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            uint result = CapturedProcess.WaitForSingleObject(process, 0);
            if (result == 0)
            {
                if (!CapturedProcess.GetExitCodeProcess(process, out uint code))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                process.Dispose();
                return code;
            }
            if (result != 258)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            await Task.Delay(ExitPollMilliseconds, token).ConfigureAwait(false);
        }
    }

    internal async Task<uint> WaitForDrainAsync(CancellationToken token)
    {
        uint code = await WaitForExitAsync(token).ConfigureAwait(false);
        await drain.WaitAsync(token).ConfigureAwait(false);
        return code;
    }

    internal void TerminateTree()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            SchedulerProcessGroup.TerminateAfterCapture(job, () =>
            {
                captureGroup();
                drain.Capture();
            });
        }
    }

    /// <summary>
    /// Jobを閉じて今回の子孫を停止します。回収完了の通知ではありません。
    /// </summary>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            try
            {
                captureGroup();
            }
            finally
            {
                disposed = true;
                lifetime.Cancel();
                job.Dispose();
                process.Dispose();
                drain.Dispose();
                lifetime.Dispose();
            }
        }
    }
}
