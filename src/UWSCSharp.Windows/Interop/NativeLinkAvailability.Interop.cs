using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class NativeLinkAvailability
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetThreadDesktop(uint thread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformationW(nint desktop,
        int index, out int information, uint length, out uint needed);
}
