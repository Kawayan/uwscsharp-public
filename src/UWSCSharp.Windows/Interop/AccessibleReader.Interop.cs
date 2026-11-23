using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

internal static partial class AccessibleReader
{
    [DllImport("oleacc.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetRoleTextW(uint role,
        StringBuilder? text, uint capacity);

    [DllImport("oleacc.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetStateTextW(uint state,
        StringBuilder? text, uint capacity);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(Native.Point point,
        [MarshalAs(UnmanagedType.Interface)] out IAccessibleNative? accessible,
        [MarshalAs(UnmanagedType.Struct)] out object child);

    [DllImport("oleacc.dll")]
    internal static extern int AccessibleObjectFromWindow(nint window, uint objectId,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessibleNative? accessible);
}
