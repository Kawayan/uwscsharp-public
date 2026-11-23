namespace UWSCSharp.Core;

/// <summary>
/// 実行中だけ適用する停止画面と停止ホットキーの設定です。
/// </summary>
public enum ScriptStopFormOption
{
    /// <summary>
    /// 停止画面を最前面へ固定します。
    /// </summary>
    TopMost,

    /// <summary>
    /// 停止ホットキーだけを無効にします。
    /// </summary>
    DisableStopHotkey
}

/// <summary>
/// 実行前の値を保持し、終了時に変更した設定を復元します。
/// </summary>
public interface IScriptStopFormOptionsSession : IAsyncDisposable
{
    /// <summary>
    /// 停止画面または停止ホットキーの設定を適用します。
    /// </summary>
    ValueTask ApplyAsync(ScriptStopFormOption option, CancellationToken cancellationToken);
}

/// <summary>
/// 実行全体で共有する停止設定の寿命を提供します。
/// </summary>
public interface IScriptStopFormOptionsHost : IScriptStopFormHost
{
    /// <summary>
    /// 実行中の停止設定に対応しているかを返します。
    /// </summary>
    bool SupportsStopFormOptions => true;

    /// <summary>
    /// 実行前の設定を保持するセッションを作成します。
    /// </summary>
    ValueTask<IScriptStopFormOptionsSession> CreateStopFormOptionsSessionAsync(
        CancellationToken cancellationToken);
}
