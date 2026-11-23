namespace UWSCSharp.IO;

/// <summary>
/// GETDIRが列挙結果を逐次取得する境界です。
/// </summary>
internal delegate IEnumerable<FileSystemInfo> DirectoryEnumerator(
    string path, string pattern, EnumerationOptions options);
