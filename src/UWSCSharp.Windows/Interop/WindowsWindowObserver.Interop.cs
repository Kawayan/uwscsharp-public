using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

public static partial class WindowsWindowObserver
{
    private sealed partial class NativePlatform
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int ReadWindowText(nint window, StringBuilder text, int count);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int ReadClassName(nint window, StringBuilder text, int count);
    }
}
