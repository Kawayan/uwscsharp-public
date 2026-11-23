using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 色深度の変換に必要なGDI資源の所有境界です。
/// </summary>
internal partial class BitmapConversionApi
{
    internal virtual nint GetDC() => ImageNative.GetDC(0);

    internal virtual void ReleaseDC(nint dc) => ImageNative.ReleaseDC(0, dc);

    internal virtual nint CreateBitmap(nint dc, PixelImage image, nint pixels)
    {
        var header = new ImageNative.BitmapHeader
        {
            Size = 40,
            Width = image.Width,
            Height = -image.Height,
            Planes = 1,
            BitCount = 32
        };
        return CreateDIBitmap(dc, ref header, 4, pixels, ref header, 0);
    }

    internal virtual int ReadBitmap(nint dc, nint bitmap, int height,
        byte[] pixels, byte[] info) =>
        GetDIBits(dc, bitmap, 0, (uint)height, pixels, info, 0);

    internal virtual void DeleteBitmap(nint bitmap) => ImageNative.DeleteObject(bitmap);
}
