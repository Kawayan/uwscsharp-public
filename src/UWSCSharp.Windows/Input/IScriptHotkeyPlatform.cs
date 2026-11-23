using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 所有スレッド上の登録と通知待機を提供します。
/// </summary>
internal interface IScriptHotkeyPlatform
{
    void Initialize();

    bool Register(int id, ScriptHotkey key);

    bool Unregister(int id);

    bool TryRead(out int id);

    void Wait(AutoResetEvent work);

    void Shutdown()
    {
    }
}
