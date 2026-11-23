namespace UWSCSharp.Windows;

/// <summary>
/// ウィンドウの現在の所有プロセスとスレッドを取得します。
/// </summary>
internal interface IWindowIdentityPlatform
{
    WindowIdentity? ReadIdentity(nint window);
}

/// <summary>
/// HWNDの再利用を検査するための所有者情報です。
/// </summary>
internal readonly record struct WindowIdentity(uint ProcessId, uint ThreadId);

/// <summary>
/// Win32からウィンドウの存在と所有者を取得します。
/// </summary>
internal sealed class NativeWindowIdentityPlatform : IWindowIdentityPlatform
{
    public WindowIdentity? ReadIdentity(nint window)
    {
        if (window == 0 || !Native.IsWindow(window))
        {
            return null;
        }

        uint thread = Native.GetWindowThreadProcessId(window, out uint process);
        return thread == 0 || process == 0 ? null : new(process, thread);
    }
}
