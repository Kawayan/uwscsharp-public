using UWSCSharp.Core;

namespace UWSCSharp.IO;

/// <summary>
/// 本体ウィンドウへ適用するINI設定を保持します。
/// </summary>
public sealed class ApplicationWindowSettings(bool stayOnTop,
    ScriptDialogFont? dialogFont, ApplicationWindowSize? mainSize,
    ApplicationWindowSize? stopSize)
{
    /// <summary>
    /// 既存のフォント設定を変更せず、最前面設定を保持します。
    /// </summary>
    public ApplicationWindowSettings(bool stayOnTop = false)
        : this(stayOnTop, null, null)
    {
    }

    /// <summary>
    /// 既存の寸法設定を変更せず、最前面とフォント設定を保持します。
    /// </summary>
    public ApplicationWindowSettings(bool stayOnTop, ScriptDialogFont? dialogFont)
        : this(stayOnTop, dialogFont, null)
    {
    }

    /// <summary>
    /// 停止画面の寸法を変更せず、本体の表示設定を保持します。
    /// </summary>
    public ApplicationWindowSettings(bool stayOnTop, ScriptDialogFont? dialogFont,
        ApplicationWindowSize? mainSize)
        : this(stayOnTop, dialogFont, mainSize, null)
    {
    }

    /// <summary>
    /// 本体を最前面に固定するかを取得します。
    /// </summary>
    public bool StayOnTop { get; } = stayOnTop;

    /// <summary>
    /// 起動時フォントを取得します。nullでの保存は既存キーを変更しません。
    /// </summary>
    public ScriptDialogFont? DialogFont { get; } = dialogFont;

    /// <summary>
    /// 本体外寸を取得します。nullでの保存は既存キーを変更しません。
    /// </summary>
    public ApplicationWindowSize? MainSize { get; } = mainSize;

    /// <summary>
    /// 停止画面の外寸を取得します。nullでの保存は既存キーを変更しません。
    /// </summary>
    public ApplicationWindowSize? StopSize { get; } = stopSize;

    /// <summary>
    /// SET/StayOnTopを読み、前後の空白を除いた値が1のときだけ有効にします。
    /// パス省略時はRoaming AppDataのUWSC/UWSC.INIを使います。
    /// </summary>
    public static async ValueTask<ApplicationWindowSettings> LoadAsync(
        string? settingsPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsPath ??= ApplicationIniPath.Default;
        if (settingsPath is null)
        {
            return new ApplicationWindowSettings();
        }

        string document = await ScriptFiles.ReadIniDocumentAsync(settingsPath,
            cancellationToken).ConfigureAwait(false);
        string value = ScriptFiles.ReadIniText(document,
            "SET", "StayOnTop", cancellationToken);
        string font = ScriptFiles.ReadIniText(document,
            "SET", Keywords.DefaultFont, cancellationToken);
        string size = ScriptFiles.ReadIniText(document,
            "SET", "MainSize", cancellationToken);
        string stop = ScriptFiles.ReadIniText(document,
            "SET", "StopSize", cancellationToken);
        return new ApplicationWindowSettings(value == "1",
            ScriptDialogFont.FromIni(font), ApplicationWindowSize.FromIni(size),
            ApplicationWindowSize.FromIni(stop));
    }

    /// <summary>
    /// 他のINI設定を残し、最前面と指定したフォント・寸法を一括保存します。
    /// </summary>
    public async ValueTask SaveAsync(string? settingsPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsPath ??= ApplicationIniPath.Default
            ?? throw new InvalidOperationException("設定の保存先を取得できませんでした。");
        string path = Path.GetFullPath(settingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var updates = new List<(string Key, string Value)>
        {
            ("StayOnTop", StayOnTop ? "1" : "0")
        };
        if (DialogFont is { } font)
        {
            updates.Add((Keywords.DefaultFont, font.OptionText));
        }

        if (MainSize is { } size)
        {
            updates.Add(("MainSize", size.IniText));
        }

        if (StopSize is { } stop)
        {
            updates.Add(("StopSize", stop.IniText));
        }

        await ScriptFiles.UpdateIniValuesAsync(path, "SET", updates,
            cancellationToken).ConfigureAwait(false);
    }
}
