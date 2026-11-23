namespace UWSCSharp.Core;

/// <summary>
/// 選択入力禁止の緊急解除に予約する組合せを定義します。
/// </summary>
public static class SelectiveInputEmergencyShortcut
{
    /// <summary>
    /// Ctrl・Alt・ShiftとF11の組合せです。
    /// </summary>
    public static ScriptHotkey Key { get; } = new(122, 7);

    /// <summary>
    /// 利用者へ表示する専用キーの名前です。
    /// </summary>
    public const string DisplayName = "Ctrl+Alt+Shift+F11";

    /// <summary>
    /// 専用予約と完全に一致するかを返します。
    /// </summary>
    public static bool IsReserved(ScriptHotkey key) => key == Key;
}
