namespace UWSCSharp.Windows;

/// <summary>
/// 宣言に書かれたDLL名を実行環境の絶対パスへ解決します。
/// </summary>
internal static class NativeDllLibraryPath
{
    internal static string Resolve(string libraryName, string workingDirectory,
        string executableDirectory, string systemDirectory)
    {
        string? root = Path.GetPathRoot(libraryName);
        string filename = Path.GetFileName(libraryName);
        if (filename is "" or "." or ".."
            || libraryName.IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0
            || libraryName.Any(char.IsControl)
            || libraryName.AsSpan(root?.Length ?? 0).Contains(':')
            || (Path.IsPathRooted(libraryName)
                && !Path.IsPathFullyQualified(libraryName)))
        {
            throw MissingLibrary(libraryName);
        }

        string path = Path.HasExtension(filename) || filename.EndsWith('.')
            ? libraryName : libraryName + ".dll";
        if (Path.IsPathFullyQualified(path))
        {
            return Path.GetFullPath(path);
        }

        string[] directories = [executableDirectory, systemDirectory,
            workingDirectory];
        foreach (string directory in directories)
        {
            string candidate = Path.GetFullPath(path, directory);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw MissingLibrary(libraryName);
    }

    private static DllNotFoundException MissingLibrary(string libraryName) =>
        new($"DLLが見つかりません: {libraryName}");
}
