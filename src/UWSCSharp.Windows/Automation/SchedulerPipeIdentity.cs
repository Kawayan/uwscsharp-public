using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

/// <summary>
/// 通知されたPIDとは別に、接続したパイプ端点をOSへ照会します。
/// </summary>
internal static partial class SchedulerPipeIdentity
{
    internal static uint Client(SafePipeHandle pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe, out uint id))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return id;
    }

    internal static uint Server(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint id))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return id;
    }
}
