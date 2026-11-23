using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 停止画面の物理座標を対象画面の作業領域へ収めます。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsStopFormLayout
{
    /// <summary>
    /// 実際の停止画面が最前面に固定されているかを取得します。
    /// </summary>
    public static bool IsTopMost(nint window)
    {
        if (!Native.IsWindow(window))
        {
            throw new InvalidOperationException("停止画面のハンドルは無効です。");
        }

        return (Native.GetWindowLongW(window, -20) & 8) != 0;
    }

    /// <summary>
    /// 表示や入力先を変更せず、最前面固定を切り替えます。
    /// </summary>
    public static void SetTopMost(nint window, bool topMost)
    {
        if (!Native.SetWindowPos(window, topMost ? -1 : -2, 0, 0, 0, 0, 0x13))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// 省略した軸を保持し、DPIの変化後も作業領域内へ配置します。
    /// </summary>
    public static void Apply(nint window, int? x, int? y)
    {
        using var dpi = new PhysicalDpiScope();
        for (int pass = 0; pass < 2; pass++)
        {
            if (!Native.GetWindowRect(window, out Native.Rect bounds))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            int requestedX = x ?? bounds.Left;
            int requestedY = y ?? bounds.Top;
            nint monitor = Native.MonitorFromPoint(new(requestedX, requestedY), 2);
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
            if (!Native.GetMonitorInfoW(monitor, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            (int left, int top) = Fit(requestedX, requestedY,
                checked(bounds.Right - bounds.Left), checked(bounds.Bottom - bounds.Top), info.Work);
            if (!Native.SetWindowPos(window, 0, left, top, 0, 0, 0x15))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    /// <summary>
    /// 必要な操作寸法を確保し、現在の画面の作業領域以内へ外寸を補正します。
    /// </summary>
    public static (int Width, int Height) ConstrainSize(nint window,
        int width, int height, int minimumWidth, int minimumHeight)
    {
        using var dpi = new PhysicalDpiScope();
        if (!Native.GetWindowRect(window, out Native.Rect bounds))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        nint monitor = Native.MonitorFromPoint(new(bounds.Left, bounds.Top), 2);
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfoW(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return ConstrainSize(width, height, minimumWidth, minimumHeight, info.Work);
    }

    internal static (int Width, int Height) ConstrainSize(int width, int height,
        int minimumWidth, int minimumHeight, Native.Rect work)
    {
        long availableWidth = (long)work.Right - work.Left;
        long availableHeight = (long)work.Bottom - work.Top;
        if (width <= 0 || height <= 0 || minimumWidth <= 0 || minimumHeight <= 0
            || availableWidth < minimumWidth || availableHeight < minimumHeight)
        {
            throw new InvalidOperationException("停止画面の操作に必要な寸法を確保できません。");
        }

        return (checked((int)Math.Clamp((long)width, minimumWidth, availableWidth)),
            checked((int)Math.Clamp((long)height, minimumHeight, availableHeight)));
    }

    internal static (int X, int Y) Fit(int x, int y, int width, int height, Native.Rect work)
    {
        long right = (long)work.Right - width;
        long bottom = (long)work.Bottom - height;
        if (width < 1 || height < 1 || right < work.Left || bottom < work.Top)
        {
            throw new InvalidOperationException("停止画面を対象画面の作業領域に収められません。");
        }

        return (checked((int)Math.Clamp((long)x, work.Left, right)),
            checked((int)Math.Clamp((long)y, work.Top, bottom)));
    }
}
