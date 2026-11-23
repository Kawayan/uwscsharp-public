using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 可視トップレベルの一覧を保持し、親と矩形をWin32から取得します。
/// </summary>
internal sealed class NativeSelectiveInputWindowTree : ISelectiveInputWindowTree
{
    private readonly HashSet<nint> visibleTopLevels;

    private NativeSelectiveInputWindowTree(HashSet<nint> visibleTopLevels)
    {
        this.visibleTopLevels = visibleTopLevels;
    }

    /// <summary>
    /// 可視トップレベル一覧を取得します。フック通知内では呼び出しません。
    /// </summary>
    internal static NativeSelectiveInputWindowTree Capture()
    {
        var windows = new HashSet<nint>();
        ExceptionDispatchInfo? callbackFailure = null;
        Native.EnumCallback callback = (window, _) =>
        {
            try
            {
                if (Native.IsWindowVisible(window))
                {
                    windows.Add(window);
                }

                return true;
            }
            catch (Exception error)
            {
                callbackFailure = ExceptionDispatchInfo.Capture(error);
                return false;
            }
        };
        Marshal.SetLastPInvokeError(0);
        bool success = Native.EnumWindows(callback, 0);
        int errorCode = Marshal.GetLastPInvokeError();
        callbackFailure?.Throw();
        if (!success)
        {
            throw new Win32Exception(errorCode,
                "入力対象のトップレベル一覧を取得できませんでした。");
        }

        return new NativeSelectiveInputWindowTree(windows);
    }

    /// <summary>
    /// 取得済みの可視一覧への所属を調べます。
    /// </summary>
    public bool IsVisibleTopLevel(nint window) =>
        visibleTopLevels.Contains(window);

    /// <summary>
    /// 現在の親、またはポップアップの所有者を取得します。
    /// </summary>
    public nint GetParent(nint window) => Native.GetParent(window);

    /// <summary>
    /// 物理座標の矩形に幅と高さがあるか調べます。取得失敗は呼出し元へ通知します。
    /// </summary>
    public bool HasArea(nint window)
    {
        using var dpi = new PhysicalDpiScope();
        if (Native.DwmGetWindowAttribute(window, 9,
            out Native.Rect bounds, Marshal.SizeOf<Native.Rect>()) != 0
            && !Native.GetWindowRect(window, out bounds))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "入力対象の親ウィンドウの矩形を取得できませんでした。");
        }

        return bounds.Left != bounds.Right && bounds.Top != bounds.Bottom;
    }
}
