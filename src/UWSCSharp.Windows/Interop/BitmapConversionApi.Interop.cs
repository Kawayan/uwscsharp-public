using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal partial class BitmapConversionApi
{
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBitmap(nint dc,
        ref ImageNative.BitmapHeader header, uint flags, nint pixels,
        ref ImageNative.BitmapHeader info, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start,
        uint lines, [Out] byte[] pixels, [In, Out] byte[] info, uint usage);
}
