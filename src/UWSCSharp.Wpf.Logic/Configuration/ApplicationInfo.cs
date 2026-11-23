namespace UWSCSharp.Wpf;

/// <summary>
/// 画面の名称と設定ファイルの保存先を共有します。
/// </summary>
internal static class ApplicationInfo
{
    internal const string Title = "UWSC#";

    internal const string PlaybackWindowTitle = Title + " 再生コントロール";

    internal static string SettingsPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UWSCSharp", fileName);
}
