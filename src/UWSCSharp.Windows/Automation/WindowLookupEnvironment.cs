using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal interface IWindowLookupEnvironment
{
    nint ForegroundWindow();

    Native.Point CursorPosition();

    nint WindowAt(Native.Point point);

    nint RootOf(nint window);
}

internal sealed class NativeWindowLookupEnvironment : IWindowLookupEnvironment
{
    /// <summary>
    /// 現在の前面ウィンドウを取得します。
    /// </summary>
    public nint ForegroundWindow() => Native.GetForegroundWindow();

    /// <summary>
    /// 共有カーソルを動かさず、画面上の座標を取得します。
    /// </summary>
    public Native.Point CursorPosition()
    {
        if (!Native.GetCursorPos(out Native.Point point))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return point;
    }

    /// <summary>
    /// 指定した画面上の点にあるウィンドウを取得します。
    /// </summary>
    public nint WindowAt(Native.Point point) => Native.WindowFromPoint(point);

    /// <summary>
    /// 所有者を含めず、親をたどって最上位を取得します。
    /// </summary>
    public nint RootOf(nint window) => Native.GetAncestor(window, 2);
}
