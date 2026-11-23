namespace UWSCSharp.Windows;

/// <summary>
/// 探索用画像の既定拡張子を解決して読み取ります。
/// </summary>
internal static class ImageTemplate
{
    internal static PixelImage Load(string path, CancellationToken token)
    {
        string resolved = Path.GetExtension(path).Length == 0
            ? path + ".bmp" : path;
        return PixelImage.LoadBmp(resolved, token);
    }
}
