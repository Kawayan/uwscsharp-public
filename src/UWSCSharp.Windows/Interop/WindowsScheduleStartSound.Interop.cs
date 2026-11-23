using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

public static partial class WindowsScheduleStartSound
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MessageBeep(uint type);
}
