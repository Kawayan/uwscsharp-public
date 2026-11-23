using System.Globalization;

namespace UWSCSharp.IO;

/// <summary>
/// 実行開始時に読み取ったログ件数と表示フォントです。
/// </summary>
public sealed class ScriptLogDefaults
{
    /// <summary>
    /// 正の件数を指定し、100000件を超える場合は上限へ補正します。
    /// </summary>
    public ScriptLogDefaults(int maximumLines = 2000)
        : this(maximumLines, new ScriptLogFont())
    {
    }

    /// <summary>
    /// 表示・保存の件数とログ表示のフォントを保持します。
    /// </summary>
    public ScriptLogDefaults(int maximumLines, ScriptLogFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        Font = font;
        MaximumLines = maximumLines <= 0 ? 2000
            : Math.Min(maximumLines, 100_000);
    }

    /// <summary>
    /// 表示履歴と保存に適用する件数を取得します。
    /// </summary>
    public int MaximumLines
    {
        get;
    }

    /// <summary>
    /// ログ表示のフォントを取得します。
    /// </summary>
    public ScriptLogFont Font
    {
        get;
    }

    /// <summary>
    /// ログ件数とフォントを同じINI文書へ一括保存します。
    /// パス省略時は読込みと同じRoaming AppData内を使います。
    /// 未知の設定を残し、保存が確定する前の失敗では元の文書を保持します。
    /// </summary>
    public async ValueTask SaveAsync(string? settingsPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsPath ??= ApplicationIniPath.Default
            ?? throw new InvalidOperationException("設定の保存先を取得できませんでした。");
        string path = Path.GetFullPath(settingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ScriptFiles.UpdateIniValuesAsync(path, "SET",
            [
                ("LogLines", MaximumLines.ToString(CultureInfo.InvariantCulture)),
                ("LogFont", Font.Family + ","
                    + Font.SizePoints.ToString("R", CultureInfo.InvariantCulture))
            ], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// UWSC.INIのSETセクションからLogLinesとLogFontを読み取ります。
    /// パス省略時はOSのRoaming AppData内のUWSCフォルダーを使います。
    /// 件数の欠落や不正値には2000件、フォントには既定値を使います。
    /// </summary>
    public static async ValueTask<ScriptLogDefaults> LoadAsync(
        string? settingsPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settingsPath is null)
        {
            settingsPath = ApplicationIniPath.Default;
            if (settingsPath is null)
            {
                return new ScriptLogDefaults();
            }
        }

        string document = await ScriptFiles.ReadIniDocumentAsync(settingsPath,
            cancellationToken).ConfigureAwait(false);
        return FromIniDocument(document, cancellationToken);
    }

    internal static ScriptLogDefaults FromIniDocument(string document,
        CancellationToken cancellationToken)
    {
        string text = ScriptFiles.ReadIniText(document,
            "SET", "LogLines", cancellationToken);
        int maximumLines = int.TryParse(text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsed) ? parsed : 2000;
        var font = ScriptLogFont.FromIni(ScriptFiles.ReadIniText(
            document, "SET", "LogFont", cancellationToken));
        return new ScriptLogDefaults(maximumLines, font);
    }
}
