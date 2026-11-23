using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class NativeInputOwnershipBackend
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool blocked);
}
