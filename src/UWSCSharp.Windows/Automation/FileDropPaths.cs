namespace UWSCSharp.Windows;

/// <summary>
/// 呼出し時の現在ディレクトリから、既存の転送対象を解決します。
/// </summary>
internal static class FileDropPaths
{
    internal static IReadOnlyList<string> Resolve(string directory,
        IReadOnlyList<string> names, string currentDirectory,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(names);
        ValidateText(directory);
        string root = Path.GetFullPath(directory, currentDirectory);
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
        {
            throw new ArgumentException("ディレクトリにはフォルダーを指定してください。", nameof(directory));
        }

        if (names.Count == 0)
        {
            throw new ArgumentException("送信するファイルがありません。", nameof(names));
        }

        var paths = new List<string>(names.Count);
        foreach (string name in names)
        {
            token.ThrowIfCancellationRequested();
            ValidateText(name);
            if (Path.IsPathRooted(name))
            {
                throw new ArgumentException("ファイル名は指定ディレクトリからの相対パスにしてください。", nameof(names));
            }

            string path = Path.GetFullPath(Path.Combine(root, name));
            _ = File.GetAttributes(path);
            paths.Add(path);
        }

        token.ThrowIfCancellationRequested();
        return paths.AsReadOnly();
    }

    private static void ValidateText(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\0'))
        {
            throw new ArgumentException("パスに空文字列やNULは使えません。");
        }
    }
}
