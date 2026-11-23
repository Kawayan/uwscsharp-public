using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// Job所属を確認したハンドルと計数の両方で終了を待ちます。
/// </summary>
internal sealed partial class SchedulerJobDrain(SafeFileHandle job) : IDisposable
{
    private const int DrainPollMilliseconds = 25;

    private readonly object sync = new();
    private readonly Dictionary<uint, SafeFileHandle> processes = [];

    internal void Capture()
    {
        lock (sync)
        {
            RemoveExited();
            foreach (uint id in SchedulerProcessGroup.ProcessIds(job))
            {
                if (processes.ContainsKey(id))
                {
                    continue;
                }
                SafeFileHandle process = OpenProcess(0x100000 | 0x1000, false, id);
                if (process.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    process.Dispose();
                    if (error is 87 or 1168)
                    {
                        continue;
                    }
                    throw new Win32Exception(error);
                }
                try
                {
                    if (!IsProcessInJob(process, job, out bool belongs))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    // PIDが再使用されても、今回のJob外のプロセスは保持しません。
                    if (belongs)
                    {
                        processes.Add(id, process);
                        process = null!;
                    }
                }
                finally
                {
                    process?.Dispose();
                }
            }
        }
    }

    internal bool KnownProcessesExited()
    {
        lock (sync)
        {
            RemoveExited();
            return processes.Count == 0;
        }
    }

    internal async Task WaitAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Capture();
            if (SchedulerProcessGroup.Count(job) == 0 && KnownProcessesExited())
            {
                return;
            }
            await Task.Delay(DrainPollMilliseconds, token).ConfigureAwait(false);
        }
    }

    private void RemoveExited()
    {
        foreach (uint id in processes.Keys.ToArray())
        {
            uint result = CapturedProcess.WaitForSingleObject(processes[id], 0);
            if (result == 0)
            {
                processes[id].Dispose();
                processes.Remove(id);
            }
            else if (result != 258)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            foreach (SafeFileHandle process in processes.Values)
            {
                process.Dispose();
            }
            processes.Clear();
        }
    }
}
