namespace UWSCSharp.Core;

/// <summary>
/// ログ表示の変更要求です。省略した位置・サイズは維持します。
/// </summary>
public sealed record ScriptLogView(bool Visible, int? X = null, int? Y = null,
    int? Width = null, int? Height = null);

/// <summary>
/// 永続ログとは独立した、ログ表示と履歴の操作を提供します。
/// </summary>
public interface IScriptLogViewHost : IScriptHost
{
    /// <summary>
    /// 実際の表示先がログウィンドウに対応しているかを返します。
    /// </summary>
    bool SupportsLogView => true;

    /// <summary>
    /// ログの表示・非表示と位置・サイズを変更します。
    /// </summary>
    ValueTask SetLogViewAsync(ScriptLogView view, CancellationToken cancellationToken);

    /// <summary>
    /// 画面用の履歴の最大件数を変更します。
    /// </summary>
    ValueTask SetLogHistoryLimitAsync(int maximumLines, CancellationToken cancellationToken);
}
