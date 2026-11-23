using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 入力フックのWin32登録結果とエラーコードを返します。
/// </summary>
internal sealed partial class NativeSelectiveInputHookBackend : ISelectiveInputHookBackend
{
    /// <summary>
    /// 指定した低レベルフックを現在のデスクトップへ登録します。
    /// </summary>
    public (nint Handle, int Error) Install(int type,
        SelectiveInputHookProcedure procedure)
    {
        if (type is not (13 or 14))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        nint hook = SetWindowsHookExW(type, procedure, Native.GetModuleHandleW(null), 0);
        return (hook, Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// 登録済みフックを解除します。
    /// </summary>
    public (bool Success, int Error) Remove(nint hook)
    {
        bool success = Native.UnhookWindowsHookEx(hook);
        return (success, Marshal.GetLastPInvokeError());
    }
}
