namespace UWSCSharp.Testing;

/// <summary>
/// テスト専用の一時ディレクトリを作成し、破棄時に中身ごと削除します。
/// </summary>
internal sealed class TemporaryTestDirectory : IDisposable
{
    public TemporaryTestDirectory(string prefix, string? guidFormat = null)
    {
        string name = prefix + Guid.NewGuid().ToString(guidFormat);
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
        Directory.CreateDirectory(Path);
    }

    public string Path
    {
        get;
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
