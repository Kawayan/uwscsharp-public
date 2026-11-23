namespace UWSCSharp.Core;

/// <summary>
/// スクリプトが登録するキーコードと修飾子の組合せです。
/// </summary>
public readonly record struct ScriptHotkey(int VirtualKey, int Modifiers);

/// <summary>
/// 一回のスクリプト実行に属するOS側のホットキー登録を管理します。
/// </summary>
public interface IScriptHotkeySource : IAsyncDisposable
{
    /// <summary>
    /// 通知処理を継続できない異常をスレッド安全に公開します。
    /// </summary>
    Exception? Failure => null;

    /// <summary>
    /// 登録または置換を試みます。失敗や例外では元の登録を維持します。
    /// 通知は任意のスレッドから届き、Procedureを直接実行しません。
    /// </summary>
    bool TryRegister(ScriptHotkey key, Action notify);

    /// <summary>
    /// 解除を試みます。失敗や例外では元の登録を維持します。
    /// </summary>
    bool TryUnregister(ScriptHotkey key);
}
