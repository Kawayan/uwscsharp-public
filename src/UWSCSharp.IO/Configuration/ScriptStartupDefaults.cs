using UWSCSharp.Core;

namespace UWSCSharp.IO;

/// <summary>
/// 同じINI文書から読み取った実行開始時の設定です。
/// </summary>
public sealed class ScriptStartupDefaults
{
    private ScriptStartupDefaults(ScriptLogDefaults log, string? defaultFont,
        ApplicationWindowSize? stopSize)
    {
        Log = log;
        DefaultFont = defaultFont;
        StopSize = stopSize;
    }

    /// <summary>
    /// ログ保存・表示の初期設定を取得します。
    /// </summary>
    public ScriptLogDefaults Log
    {
        get;
    }

    /// <summary>
    /// 初期フォントのOPTION形式を取得します。未設定ではnullです。
    /// </summary>
    public string? DefaultFont
    {
        get;
    }

    /// <summary>
    /// 実行で使う停止画面の物理外寸を取得します。未設定ではnullです。
    /// </summary>
    public ApplicationWindowSize? StopSize
    {
        get;
    }

    /// <summary>
    /// Roaming AppDataのUWSC/UWSC.INI、または指定したINIを一度読みます。
    /// 読込み失敗と取消は呼出し元へ通知し、ファイルは変更しません。
    /// </summary>
    public static async ValueTask<ScriptStartupDefaults> LoadAsync(
        string? settingsPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsPath ??= ApplicationIniPath.Default;
        string document = settingsPath is null ? ""
            : await ScriptFiles.ReadIniDocumentAsync(settingsPath,
                cancellationToken).ConfigureAwait(false);
        string text = ScriptFiles.ReadIniText(document,
            "SET", Keywords.DefaultFont, cancellationToken);
        string stopSize = ScriptFiles.ReadIniText(document,
            "SET", "StopSize", cancellationToken);
        return new ScriptStartupDefaults(
            ScriptLogDefaults.FromIniDocument(document, cancellationToken),
            ScriptDialogFont.FromIni(text)?.OptionText,
            ApplicationWindowSize.FromIni(stopSize));
    }
}
