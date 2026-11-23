using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// 継承するNULと、作成時に所属するJobだけを作成属性へ渡します。
/// </summary>
internal sealed class SchedulerProcessAttributes : IDisposable
{
    private readonly List<SafeFileHandle> retained = [];
    private nint handles;
    private nint jobs;
    private bool initialized;

    internal nint Pointer
    {
        get; private set;
    }

    internal SchedulerProcessAttributes(SafeFileHandle sink,
        SafeFileHandle monitorJob, SafeFileHandle executionJob)
    {
        try
        {
            foreach (SafeFileHandle handle in new[] { sink, monitorJob, executionJob })
            {
                bool success = false;
                handle.DangerousAddRef(ref success);
                if (success)
                {
                    retained.Add(handle);
                }
            }
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(Pointer, 2, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            initialized = true;
            handles = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(handles, sink.DangerousGetHandle());
            jobs = Marshal.AllocHGlobal(2 * nint.Size);
            Marshal.WriteIntPtr(jobs, monitorJob.DangerousGetHandle());
            Marshal.WriteIntPtr(jobs, nint.Size, executionJob.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(Pointer, 0, 0x20002, handles,
                (nuint)nint.Size, 0, 0)
                || !UpdateProcThreadAttribute(Pointer, 0, 0x2000d, jobs,
                    (nuint)(2 * nint.Size), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (initialized)
        {
            DeleteProcThreadAttributeList(Pointer);
            initialized = false;
        }
        Marshal.FreeHGlobal(handles);
        Marshal.FreeHGlobal(jobs);
        Marshal.FreeHGlobal(Pointer);
        handles = jobs = Pointer = 0;
        foreach (SafeFileHandle handle in retained)
        {
            handle.DangerousRelease();
        }
        retained.Clear();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list,
        int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list,
        uint flags, nuint attribute, nint value, nuint size,
        nint previous, nint returnedSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
}
