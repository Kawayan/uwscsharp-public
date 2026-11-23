using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class NativeListControl
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size,
        uint allocationType, uint protection);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
