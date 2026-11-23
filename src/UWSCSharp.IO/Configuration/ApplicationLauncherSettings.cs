using System.Globalization;

namespace UWSCSharp.IO;

/// <summary>
/// アプリケーションINIのランチメニュー位置を保持します。
/// </summary>
public sealed class ApplicationLauncherSettings
{
    /// <summary>
    /// 座標を保持し、両方が-1の場合はカーソル位置を使います。
    /// </summary>
    public ApplicationLauncherSettings(int x = -1, int y = -1)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// 指定された横座標を取得します。
    /// </summary>
    public int X
    {
        get;
    }

    /// <summary>
    /// 指定された縦座標を取得します。
    /// </summary>
    public int Y
    {
        get;
    }

    /// <summary>
    /// 固定座標の代わりにカーソル位置を使うかを取得します。
    /// </summary>
    public bool UseCursorPosition => X == -1 && Y == -1;

    /// <summary>
    /// 整数2項目のPositionを読み、不正値はカーソル位置へ戻します。
    /// </summary>
    public static async ValueTask<ApplicationLauncherSettings> LoadAsync(
        string? settingsPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsPath ??= ApplicationIniPath.Default;
        string document = settingsPath is null ? ""
            : await ScriptFiles.ReadIniDocumentAsync(settingsPath,
                cancellationToken).ConfigureAwait(false);
        string text = ScriptFiles.ReadIniText(document,
            "LUNCHMENU", "Position", cancellationToken);
        string[] fields = text.Split(',');
        if (fields.Length != 2
            || !int.TryParse(fields[0], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(fields[1], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int y))
        {
            return new ApplicationLauncherSettings();
        }

        return new ApplicationLauncherSettings(x, y);
    }

    /// <summary>
    /// 他のキーを保持してPositionだけを保存します。
    /// </summary>
    public async ValueTask SaveAsync(string? settingsPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsPath ??= ApplicationIniPath.Default
            ?? throw new InvalidOperationException("設定の保存先を取得できませんでした。");
        string path = Path.GetFullPath(settingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string position = X.ToString(CultureInfo.InvariantCulture)
            + "," + Y.ToString(CultureInfo.InvariantCulture);
        await ScriptFiles.UpdateIniValuesAsync(path, "LUNCHMENU",
            [("Position", position)], cancellationToken).ConfigureAwait(false);
    }
}
