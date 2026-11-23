using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// 起動したプロセスのハンドルを所有し、終了とは独立して解放します。
/// </summary>
internal sealed partial class ExecProcess : IDisposable
{
    private readonly SafeFileHandle handle;

    internal uint Id
    {
        get;
    }

    private ExecProcess(nint process, uint id)
    {
        handle = new SafeFileHandle(process, true);
        Id = id;
    }

    internal static ExecProcess Start(string command)
    {
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>()
        };
        if (!CreateProcessW(null, new StringBuilder(command), 0, 0,
            false, 0, 0, null, ref startup, out ProcessInfo process))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        using var thread = new SafeFileHandle(process.Thread, true);
        return new ExecProcess(process.Process, process.ProcessId);
    }

    internal bool HasExited
    {
        get
        {
            uint result = WaitForSingleObject(handle, 0);
            return result switch
            {
                0 => true,
                258 => false,
                _ => throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()))
            };
        }
    }

    internal uint ExitCode()
    {
        if (!GetExitCodeProcess(handle, out uint code))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return code;
    }

    /// <summary>
    /// プロセスを終了させず、所有ハンドルだけを閉じます。
    /// </summary>
    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
