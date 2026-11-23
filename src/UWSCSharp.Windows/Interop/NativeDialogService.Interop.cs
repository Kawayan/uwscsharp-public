using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

internal sealed partial class NativeDialogService
{
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int BalloonSetWindowLongW(nint window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetParent", SetLastError = true)]
    private static extern nint BalloonSetParent(nint window, nint parent);

    [DllImport("user32.dll", EntryPoint = "GetDesktopWindow")]
    private static extern nint BalloonGetDesktopWindow();

    [DllImport("user32.dll", EntryPoint = "SetLayeredWindowAttributes", SetLastError = true)]
    private static extern bool BalloonSetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);

    [DllImport("gdi32.dll", EntryPoint = "CreatePolygonRgn")]
    private static extern nint BalloonCreatePolygonRgn(Native.Point[] points, int count, int mode);

    [DllImport("user32.dll", EntryPoint = "SetWindowRgn", SetLastError = true)]
    private static extern int BalloonSetWindowRgn(nint window, nint region, bool redraw);

    [DllImport("user32.dll", EntryPoint = "InvalidateRect")]
    private static extern bool BalloonInvalidateRect(nint window, nint rect, bool erase);

    [DllImport("user32.dll", EntryPoint = "BeginPaint")]
    private static extern nint BalloonBeginPaint(nint window, out BalloonPaint paint);

    [DllImport("user32.dll", EntryPoint = "EndPaint")]
    private static extern bool BalloonEndPaint(nint window, ref BalloonPaint paint);

    [DllImport("gdi32.dll", EntryPoint = "CreateSolidBrush")]
    private static extern nint BalloonCreateSolidBrush(uint color);

    [DllImport("gdi32.dll", EntryPoint = "GetStockObject")]
    private static extern nint BalloonGetStockObject(int index);

    [DllImport("gdi32.dll", EntryPoint = "SetDCPenColor")]
    private static extern uint BalloonSetDCPenColor(nint dc, uint color);

    [DllImport("gdi32.dll", EntryPoint = "Polygon")]
    private static extern bool BalloonPolygon(nint dc, Native.Point[] points, int count);

    [DllImport("gdi32.dll", EntryPoint = "SetBkMode")]
    private static extern int BalloonSetBkMode(nint dc, int mode);

    [DllImport("gdi32.dll", EntryPoint = "SetTextColor")]
    private static extern uint BalloonSetTextColor(nint dc, uint color);

    [DllImport("gdi32.dll", EntryPoint = "SaveDC")]
    private static extern int BalloonSaveDC(nint dc);

    [DllImport("gdi32.dll", EntryPoint = "RestoreDC")]
    private static extern bool BalloonRestoreDC(nint dc, int saved);

    [DllImport("gdi32.dll", EntryPoint = "IntersectClipRect")]
    private static extern int BalloonIntersectClipRect(nint dc, int left, int top, int right, int bottom);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure callback,
        nuint subclass, nuint reference);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint subclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(nint window, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(nint drop, uint index, StringBuilder? text, uint count);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(nint drop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string name, nint file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(nint module);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRectEx(ref Native.Rect rect, uint style, bool menu, uint extended);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(nint window, string text);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(nint dc, string text, int length, ref Native.Rect rect, uint flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFontW(int height, int width, int escape, int orientation,
        int weight, uint italic, uint underline, uint strike, uint charset, uint precision,
        uint clip, uint quality, uint pitch, string face);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetTextExtentPoint32W(nint dc, string text, int count, out Size size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [DllImport("user32.dll")]
    private static extern bool EndMenu();

    [DllImport("comctl32.dll")]
    private static extern bool InitCommonControlsEx(ref CommonControls controls);

    [DllImport("user32.dll")]
    private static extern int SetScrollInfo(nint window, int bar, ref ScrollInfo info, bool redraw);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetScrollInfo(nint window, int bar, ref ScrollInfo info);

    [DllImport("user32.dll", EntryPoint = "EnableWindow")]
    private static extern bool StopFormEnableWindow(nint window, bool enabled);

    [DllImport("gdi32.dll", EntryPoint = "GetGlyphIndicesW", CharSet = CharSet.Unicode)]
    private static extern uint StopFormGetGlyphIndicesW(nint dc, string text, int count,
        [Out] ushort[] glyphs, uint flags);
}
