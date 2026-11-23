using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

/// <summary>
/// ネイティブSysLinkの既定操作を入力デスクトップに限定します。
/// </summary>
internal static partial class NativeLinkAvailability
{
    internal static bool CanInvoke(nint window)
    {
        var name = new StringBuilder(256);
        if (Native.GetClassNameW(window, name, name.Capacity) == 0)
        {
            return false;
        }

        if (!ControlClassNames.Matches(name.ToString(), ControlClassNames.Link))
        {
            return true;
        }

        uint thread = Native.GetWindowThreadProcessId(window, out _);
        if (thread == 0)
        {
            return false;
        }

        // GetThreadDesktopの借用ハンドルは閉じません。
        nint desktop = GetThreadDesktop(thread);
        if (desktop == 0 || !GetUserObjectInformationW(desktop, 6,
            out int input, sizeof(int), out _))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return input != 0;
    }
}
