namespace UWSCSharp.Core;

/// <summary>
/// 実行中に変更するログ設定です。nullの項目は変更しません。
/// </summary>
public sealed record ScriptLogSettings(string? Path = null,
    int? MaximumLines = null, int? FileMode = null);

/// <summary>
/// ログ設定の変更に対応する出力先です。
/// </summary>
public interface IScriptLogHost : IScriptHost
{
    /// <summary>
    /// 設定変更前の未保存出力を保存し、新しい設定を適用します。
    /// </summary>
    ValueTask ConfigureLogAsync(ScriptLogSettings settings,
        CancellationToken cancellationToken);
}
