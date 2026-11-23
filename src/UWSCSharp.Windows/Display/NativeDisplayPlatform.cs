using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 同期処理のDPI認識を復元し、現在の物理画面情報を取得します。
/// </summary>
internal sealed partial class NativeDisplayPlatform : IDisplayPlatform
{
    public IReadOnlyList<DisplayMonitor> Enumerate(CancellationToken token)
    {
        using var dpi = new PhysicalDpiScope();
        List<DisplayMonitor> monitors = [];
        Exception? failure = null;
        bool success = Native.EnumDisplayMonitors(0, 0,
            (nint monitor, nint dc, ref Native.Rect rect, nint data) =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var info = new MonitorInfo
                    {
                        Size = Marshal.SizeOf<MonitorInfo>(),
                        Device = ""
                    };
                    Check(GetMonitorInfoW(monitor, ref info));
                    var device = new DisplayDevice
                    {
                        Size = Marshal.SizeOf<DisplayDevice>(),
                        DeviceName = "",
                        DeviceString = "",
                        DeviceId = "",
                        DeviceKey = ""
                    };
                    string name = EnumDisplayDevicesW(info.Device, 0, ref device, 0)
                        ? device.DeviceString : "";
                    monitors.Add(new(monitor, info.Monitor.Left, info.Monitor.Top,
                        checked(info.Monitor.Right - info.Monitor.Left),
                        checked(info.Monitor.Bottom - info.Monitor.Top),
                        name, (info.Flags & 1) != 0));
                    return true;
                }
                catch (Exception error)
                {
                    // 管理例外をネイティブのcallback境界へ流しません。
                    failure = error;
                    return false;
                }
            }, 0);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (!success)
        {
            throw WindowsOperationErrors.Record(new InvalidOperationException(
                "モニターを列挙できませんでした。"));
        }

        token.ThrowIfCancellationRequested();
        return monitors;
    }

    public Native.Point CursorPosition()
    {
        // DPI認識の切替に依存せず、物理座標を直接取得します。
        Check(Native.GetPhysicalCursorPos(out Native.Point point));
        return point;
    }

    public int BitsPerPixel()
    {
        using var dpi = new PhysicalDpiScope();
        nint dc = ImageNative.GetDC(0);
        if (dc == 0)
        {
            throw WindowsOperationErrors.Record(new InvalidOperationException(
                "主画面のDCを取得できませんでした。"));
        }

        try
        {
            return checked(GetDeviceCaps(dc, 12) * GetDeviceCaps(dc, 14));
        }
        finally
        {
            ImageNative.ReleaseDC(0, dc);
        }
    }

    private static void Check(bool success)
    {
        if (!success)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Native.Rect Monitor;
        internal Native.Rect Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        internal int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceString;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string DeviceKey;
    }
}
