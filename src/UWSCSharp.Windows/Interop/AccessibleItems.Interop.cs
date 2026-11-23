using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class AccessibleItems
{
    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(IAccessibleNative accessible,
        int start, int count, nint children, out int obtained);

    [DllImport("oleacc.dll")]
    private static extern int WindowFromAccessibleObject(IAccessibleNative accessible,
        out nint window);
}
