using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UWSCSharp.Windows;

public sealed partial class WindowsScheduleWakeTimer
{
    private sealed partial class NativePlatform
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(
            nint attributes, string? name, uint flags, uint access);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer,
            ref long dueTime, int period, nint callback, nint state, bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
    }
}
