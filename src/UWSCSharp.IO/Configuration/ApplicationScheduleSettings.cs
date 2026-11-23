using System.Globalization;

namespace UWSCSharp.IO;

/// <summary>
/// アプリケーションINIの予約容量を保持します。
/// </summary>
public sealed class ApplicationScheduleSettings
{
    /// <summary>
    /// 既存の予約画面と同じ40～400枠の容量を保持します。
    /// </summary>
    public ApplicationScheduleSettings(int capacity = 40)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 40);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 400);
        Capacity = capacity;
    }

    /// <summary>
    /// 予約枠の数を取得します。
    /// </summary>
    public int Capacity
    {
        get;
    }

    /// <summary>
    /// MaxNumberを読み、不正値は40、範囲外の整数は40～400へ補正します。
    /// </summary>
    public static async ValueTask<ApplicationScheduleSettings> LoadAsync(
        string? settingsPath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsPath ??= ApplicationIniPath.Default;
        string document = settingsPath is null ? ""
            : await ScriptFiles.ReadIniDocumentAsync(settingsPath,
                cancellationToken).ConfigureAwait(false);
        string text = ScriptFiles.ReadIniText(document,
            "SCHEDULE", "MaxNumber", cancellationToken);
        int capacity = int.TryParse(text, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int parsed) ? Math.Clamp(parsed, 40, 400) : 40;
        return new ApplicationScheduleSettings(capacity);
    }

    /// <summary>
    /// 他のキーを保持してMaxNumberだけを保存します。
    /// </summary>
    public async ValueTask SaveAsync(string? settingsPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsPath ??= ApplicationIniPath.Default
            ?? throw new InvalidOperationException("設定の保存先を取得できませんでした。");
        string path = Path.GetFullPath(settingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ScriptFiles.UpdateIniValuesAsync(path, "SCHEDULE",
            [("MaxNumber", Capacity.ToString(CultureInfo.InvariantCulture))],
            cancellationToken).ConfigureAwait(false);
    }
}
