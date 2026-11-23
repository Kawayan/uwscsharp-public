using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 低レベルキーボードフックのWin32操作を実行します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NativeModifierHookBackend : IModifierHookBackend
{
    private const int InvalidHookHandle = 1404;

    public nint Install(ModifierHookProcedure procedure) =>
        SetWindowsHookExW(13, procedure, Native.GetModuleHandleW(null), 0);

    public bool Remove(nint hook)
    {
        // OSが無効と確認したフックは、すでに解除されています。
        return Native.UnhookWindowsHookEx(hook)
            || Marshal.GetLastPInvokeError() == InvalidHookHandle;
    }

    public bool IsPressed(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    public nint Next(int code, nuint message, nint data) =>
        Native.CallNextHookEx(0, code, message, data);
}
