namespace UWSCSharp.IO;

/// <summary>
/// アプリケーション設定のINI保存先を共有します。
/// </summary>
internal static class ApplicationIniPath
{
    internal static string? Default
    {
        get
        {
            string appData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            return string.IsNullOrWhiteSpace(appData) ? null
                : Path.Combine(appData, "UWSC", "UWSC.INI");
        }
    }
}
