using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class JpegWriter
{
    [DllImport("gdiplus.dll")]
    internal static extern int GdiplusStartup(out nuint token, ref StartupInput input, nint output);

    [DllImport("gdiplus.dll")]
    internal static extern void GdiplusShutdown(nuint token);

    [DllImport("gdiplus.dll")]
    private static extern int GdipCreateBitmapFromScan0(int width, int height,
        int stride, int pixelFormat, nint pixels, out nint bitmap);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    private static extern int GdipSaveImageToFile(nint image, string path,
        ref Guid encoder, ref EncoderParameters parameters);

    [DllImport("gdiplus.dll")]
    internal static extern int GdipDisposeImage(nint image);
}
