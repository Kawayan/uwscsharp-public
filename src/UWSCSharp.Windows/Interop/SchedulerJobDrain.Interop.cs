using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

internal sealed partial class SchedulerJobDrain
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeFileHandle process, SafeFileHandle job,
        out bool belongs);
}
