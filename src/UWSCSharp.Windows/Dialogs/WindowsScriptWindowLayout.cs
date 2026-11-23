using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 停止画面とログ画面の物理座標を計算します。
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsScriptWindowLayout
{
    /// <summary>
    /// 表示状態を変えず、通常表示時の物理外枠を取得します。
    /// </summary>
    public static (int X, int Y, int Width, int Height) ReadBounds(nint window)
    {
        using var dpi = new PhysicalDpiScope();
        Native.Rect rect;
        if (Native.IsIconic(window) || Native.IsZoomed(window))
        {
            var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(window, ref placement))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            rect = placement.NormalPosition;
            if ((Native.GetWindowLongW(window, -20) & 0x80) == 0)
            {
                Native.MonitorInfo info = MonitorInfo(Native.MonitorFromWindow(window, 2));
                int dx = checked(info.Work.Left - info.Monitor.Left);
                int dy = checked(info.Work.Top - info.Monitor.Top);
                rect = new Native.Rect
                {
                    Left = checked(rect.Left + dx),
                    Top = checked(rect.Top + dy),
                    Right = checked(rect.Right + dx),
                    Bottom = checked(rect.Bottom + dy)
                };
            }
        }
        else if (!Native.GetWindowRect(window, out rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (rect.Left, rect.Top, checked(rect.Right - rect.Left),
            checked(rect.Bottom - rect.Top));
    }

    /// <summary>
    /// 停止画面の通常外寸を保ち、作業領域内へ指定位置を補正します。
    /// </summary>
    public static (int X, int Y, int Width, int Height) StopBounds(
        nint window, ScriptWindowPosition request)
    {
        using var dpi = new PhysicalDpiScope();
        (int _, int _, int width, int height) = ReadBounds(window);
        Native.Rect work = MonitorInfo(
            Native.MonitorFromPoint(new(request.X, request.Y), 2)).Work;
        (int x, int y) = WindowsStopFormLayout.Fit(
            request.X, request.Y, width, height, work);
        return (x, y, width, height);
    }

    /// <summary>
    /// ログ画面を停止画面の上、収まらなければ下へ配置します。
    /// </summary>
    public static (int X, int Y) LogPosition(
        (int X, int Y, int Width, int Height) stop, int width, int height)
    {
        using var dpi = new PhysicalDpiScope();
        Native.Rect work = MonitorInfo(
            Native.MonitorFromPoint(new(stop.X, stop.Y), 2)).Work;
        return LogPosition(stop, width, height, work);
    }

    internal static (int X, int Y) LogPosition(
        (int X, int Y, int Width, int Height) stop,
        int width, int height, Native.Rect work)
    {
        if (width <= 0 || height <= 0 || stop.Width <= 0 || stop.Height <= 0
            || work.Right <= work.Left || work.Bottom <= work.Top)
        {
            throw new InvalidOperationException("画面の配置に必要な寸法が不正です。");
        }

        long above = (long)stop.Y - height;
        long y = above >= work.Top ? above : (long)stop.Y + stop.Height;
        // 大きすぎるログ画面は寸法を変えず、左上を操作できる位置へ置きます。
        long right = Math.Max(work.Left, (long)work.Right - width);
        long bottom = Math.Max(work.Top, (long)work.Bottom - height);
        return (checked((int)Math.Clamp((long)stop.X, work.Left, right)),
            checked((int)Math.Clamp(y, work.Top, bottom)));
    }

    private static Native.MonitorInfo MonitorInfo(nint monitor)
    {
        var info = new Native.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<Native.MonitorInfo>()
        };
        if (!Native.GetMonitorInfoW(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length;
        public uint Flags;
        public uint ShowCommand;
        public Native.Point MinimumPosition;
        public Native.Point MaximumPosition;
        public Native.Rect NormalPosition;
    }
}
