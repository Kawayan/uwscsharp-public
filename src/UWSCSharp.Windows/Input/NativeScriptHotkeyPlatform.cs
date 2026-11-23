using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// ウィンドウを作らず、所有スレッドでWM_HOTKEYを受信します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NativeScriptHotkeyPlatform : IScriptHotkeyPlatform
{
    private const uint HotkeyMessage = 0x0312;
    private readonly NativeModifierHotkeys modifiers;
    private uint threadId;

    internal bool IsModifierCallbackRooted => modifiers.IsCallbackRooted;

    internal NativeScriptHotkeyPlatform(IModifierHookBackend? modifierBackend = null)
    {
        modifiers = new NativeModifierHotkeys(id =>
        {
            if (!Native.PostThreadMessageW(threadId, HotkeyMessage, (nuint)id, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }, modifierBackend);
    }

    public void Initialize()
    {
        threadId = Native.GetCurrentThreadId();
        Native.PeekMessageW(out _, 0, 0, 0, 0);
    }

    public bool Register(int id, ScriptHotkey key) =>
        key.VirtualKey == 0 ? modifiers.Register(id, key.Modifiers)
            : RegisterHotKey(0, id, (uint)key.Modifiers, (uint)key.VirtualKey);

    public bool Unregister(int id) => modifiers.Contains(id)
        ? modifiers.Unregister(id) : UnregisterHotKey(0, id);

    public void Shutdown() => modifiers.Shutdown();

    public bool TryRead(out int id)
    {
        modifiers.ThrowIfFaulted();
        bool received = Native.PeekMessageW(out Native.Message message,
            -1, HotkeyMessage, HotkeyMessage, 1);
        modifiers.ThrowIfFaulted();
        id = received ? checked((int)message.WParam) : 0;
        return received;
    }

    public void Wait(AutoResetEvent work)
    {
        // 呼出し元がスレッド終了までイベントを所有するため、待機中には閉じません。
        nint[] handles = [work.SafeWaitHandle.DangerousGetHandle()];
        uint result = Native.MsgWaitForMultipleObjectsEx(1, handles,
            uint.MaxValue, 0x04FF, 0x0004);
        GC.KeepAlive(work);
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
