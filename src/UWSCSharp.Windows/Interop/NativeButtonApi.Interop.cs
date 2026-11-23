using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class NativeButtonApi
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string name);
}
