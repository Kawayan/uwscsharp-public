using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed partial class BalloonTextLayout
{
    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateFromHDC(nint dc, out nint graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetPageUnit(nint graphics, int unit);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetTextRenderingHint(nint graphics, int hint);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateFontFromDC(nint dc, out nint font);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateStringFormat(int flags, ushort language, out nint format);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateSolidFill(uint argb, out nint brush);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetSolidFillColor(nint brush, uint argb);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetClipRect(nint graphics, float x, float y,
        float width, float height, int mode);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GdipMeasureString(nint graphics, string text,
        int length, nint font, ref RectangleF bounds, nint format,
        out RectangleF measured, out int fitted, out int lines);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GdipDrawString(nint graphics, string text,
        int length, nint font, ref RectangleF bounds, nint format, nint brush);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteBrush(nint brush);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteStringFormat(nint format);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteFont(nint font);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteGraphics(nint graphics);
}
