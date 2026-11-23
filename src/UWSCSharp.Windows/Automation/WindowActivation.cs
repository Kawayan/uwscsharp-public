using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

internal interface IWindowActivation
{
    /// <summary>
    /// 対象を通常表示へ戻します。
    /// </summary>
    void ShowNormal(nint window);

    /// <summary>
    /// 前面化を要求し、OSが受理したかを返します。
    /// </summary>
    bool RequestForeground(nint window);
}

[SupportedOSPlatform("windows")]
internal sealed class NativeWindowActivation : IWindowActivation
{
    /// <summary>
    /// 位置変更に先立って対象を通常表示へ戻します。
    /// </summary>
    public void ShowNormal(nint window) => Native.ShowWindow(window, 1);

    /// <summary>
    /// システム全体の設定を変更せず前面化を要求します。
    /// </summary>
    public bool RequestForeground(nint window) => Native.SetForegroundWindow(window);
}
