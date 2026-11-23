namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 集約ビルド出力からテスト用プロセスを探します。
/// </summary>
internal static class FixturePaths
{
    internal static string Get(string project, string fileName)
    {
        string configuration = new DirectoryInfo(
            AppContext.BaseDirectory).Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", project, configuration, fileName));
    }
}
