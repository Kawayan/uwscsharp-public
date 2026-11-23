using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// 監視所有者が、予約専用の名前付きJobを回収してから起動を許可します。
/// </summary>
/// <remarks>呼出し元は同じ識別子の監視所有ハンドルを保持してください。</remarks>
internal sealed partial class SchedulerProcessGroup : IDisposable
{
    private static readonly TimeSpan RecoveryDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly object sync = new();
    private readonly SafeFileHandle job;
    private readonly SchedulerJobDrain drain;
    private bool ready;
    private bool recovering;
    private bool disposed;

    internal SchedulerProcessGroup(Guid identity, SafeFileHandle monitorFile)
    {
        if (identity == Guid.Empty)
        {
            throw new ArgumentException("予約Jobの識別子は空にできません。", nameof(identity));
        }
        using var current = WindowsIdentity.GetCurrent();
        string sid = current.User?.Value
            ?? throw new InvalidOperationException("予約Jobの利用者SIDを取得できません。");
        if (!GetFileInformationByHandleEx(monitorFile, 18, out FileIdentity file,
            (uint)Marshal.SizeOf<FileIdentity>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (file.Id == Guid.Empty)
        {
            throw new NotSupportedException("監視ファイルの識別子を取得できません。");
        }
        string scope = $"{sid}|{identity:N}|{file.Volume:x16}|{file.Id:N}";
        string name = "Global\\UWSCSharp-schedule-"
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        job = CreateJobObjectW(0, name);
        if (job.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw error;
        }
        try
        {
            OwnedProcessJob.EnableKillOnClose(job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
        drain = new SchedulerJobDrain(job);
    }

    internal uint ActiveProcesses
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return Count(job);
            }
        }
    }

    internal async Task RecoverAsync(CancellationToken token = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (recovering || ready)
            {
                throw new InvalidOperationException("予約Jobの復旧は開始前に一度だけ行います。");
            }
            recovering = true;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if (ActiveProcesses != 0)
            {
                TerminateAfterCapture(job, drain.Capture);
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(RecoveryDrainTimeout);
            await WaitForDrainAsync(deadline.Token).ConfigureAwait(false);
            lock (sync)
            {
                ready = true;
            }
        }
        finally
        {
            lock (sync)
            {
                recovering = false;
            }
        }
    }

    /// <summary>
    /// 診断用の所属PIDのスナップショットを返します。終了の証明には使いません。
    /// </summary>
    internal IReadOnlyList<uint> GetProcessIds()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return ProcessIds(job);
        }
    }

    internal static IReadOnlyList<uint> ProcessIds(SafeFileHandle job)
    {
        const int capacity = 4096;
        int size = 8 + capacity * nint.Size;
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryProcessIds(job, 3, buffer, (uint)size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            int count = Marshal.ReadInt32(buffer, 4);
            if (count < 0 || count > capacity)
            {
                throw new InvalidDataException("予約JobのPID一覧が上限を超えました。");
            }
            uint[] ids = new uint[count];
            for (int index = 0; index < count; index++)
            {
                ids[index] = checked((uint)Marshal.ReadIntPtr(buffer,
                    8 + index * nint.Size));
            }
            return Array.AsReadOnly(ids);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal SchedulerChildProcess Start(string executable,
        IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken token = default, SchedulerProcessCreation? creation = null)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!ready || recovering)
            {
                throw new InvalidOperationException("予約Jobの回収を確認していません。");
            }
            return SchedulerChildProcess.Start(executable, arguments,
                workingDirectory, job, drain.Capture, token, creation);
        }
    }

    internal async Task WaitForDrainAsync(CancellationToken token)
    {
        await drain.WaitAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            if (recovering || Count(job) != 0 || !drain.KnownProcessesExited())
            {
                throw new InvalidOperationException("予約Jobの回収完了まで所有を解放できません。");
            }
            drain.Dispose();
            job.Dispose();
            disposed = true;
        }
    }

    internal static uint Count(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, 1, out JobAccounting information,
            (uint)Marshal.SizeOf<JobAccounting>(), 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return information.ActiveProcesses;
    }

    internal static void Terminate(SafeFileHandle job)
    {
        if (!TerminateJobObject(job, 130))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static void TerminateAfterCapture(SafeFileHandle job, Action capture)
    {
        Exception? failure = null;
        try
        {
            capture();
        }
        catch (Exception error)
        {
            failure = error;
        }
        try
        {
            Terminate(job);
        }
        catch (Exception error) when (failure is not null)
        {
            throw new AggregateException("予約Jobの診断と停止の両方が失敗しました。", failure, error);
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
