using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 同期処理の座標を物理ピクセルへ統一します。
/// </summary>
internal readonly struct PhysicalDpiScope : IDisposable
{
    private readonly nint previous;

    /// <summary>
    /// 現在のスレッドをPer Monitor V2へ切り替えます。
    /// </summary>
    public PhysicalDpiScope()
    {
        previous = Native.SetThreadDpiAwarenessContext(-4);
        if (previous == 0)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    /// <summary>
    /// 呼出し元のスレッドDPI認識を復元します。
    /// </summary>
    public void Dispose()
    {
        Native.SetThreadDpiAwarenessContext(previous);
    }
}
