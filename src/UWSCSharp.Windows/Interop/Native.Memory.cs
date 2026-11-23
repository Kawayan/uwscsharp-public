using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class Native
{
    [DllImport("kernel32.dll")]
    internal static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint VirtualAllocEx(nint process, nint address,
        nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool VirtualFreeEx(nint process, nint address,
        nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadProcessMemory(nint process, nint address,
        [Out] byte[] bytes, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteProcessMemory(nint process, nint address,
        byte[] bytes, nuint size, out nuint written);

    [DllImport("oleaut32.dll")]
    internal static extern int VariantClear(nint variant);
}
