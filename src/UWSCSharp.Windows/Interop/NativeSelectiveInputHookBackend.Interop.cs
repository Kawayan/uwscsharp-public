using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeSelectiveInputHookBackend
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int type,
        SelectiveInputHookProcedure callback, nint module, uint thread);
}
