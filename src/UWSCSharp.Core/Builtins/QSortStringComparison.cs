using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Core;

/// <summary>
/// QSORTのWindows文字列比較を提供します。
/// </summary>
internal static class QSortStringComparison
{
    /// <summary>
    /// 原版と同じAPIで文字列の順序を比較します。
    /// </summary>
    internal static int Compare(string left, string right, bool natural)
    {
        if (natural)
        {
            return StrCmpLogicalW(left, right);
        }

        int result = CompareStringW(0x400, 0,
            left, left.Length, right, right.Length);
        if (result == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return result - 2;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern int CompareStringW(uint locale, uint flags,
        string left, int leftLength, string right, int rightLength);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string left, string right);
}
