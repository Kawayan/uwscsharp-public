namespace UWSCSharp.Windows;

/// <summary>
/// packed DIBを画像共通のデコーダーへ渡します。
/// </summary>
internal static class ClipboardDib
{
    internal static PixelImage Decode(byte[] data, CancellationToken token) =>
        DibDecoder.Decode(data, token);
}
