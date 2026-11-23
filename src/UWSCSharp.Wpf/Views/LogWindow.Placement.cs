using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

public partial class LogWindow
{
    private const int ExtendedStyleIndex = -20;
    private const int ToolWindowStyle = 0x0080;
    private const uint NearestMonitor = 2;
    private const uint HiddenShowCommand = 0;
    private NativeRect? requestedRestoredBounds;

    internal void PositionBesideStop((int X, int Y, int Width, int Height) stop)
    {
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        (int _, int _, int width, int height) = WindowsScriptWindowLayout.ReadBounds(handle);
        if (requestedRestoredBounds is { } pending)
        {
            width = checked(pending.Right - pending.Left);
            height = checked(pending.Bottom - pending.Top);
        }

        (int x, int y) = WindowsScriptWindowLayout.LogPosition(stop, width, height);
        if (WindowState != WindowState.Normal)
        {
            // 表示状態は変更せず、既存の通常表示への復元処理へ渡します。
            ApplyRestoredBounds(new ScriptLogView(IsVisible, x, y), updatePlacement: false);
            return;
        }

        requestedRestoredBounds = null;
        SetPhysicalBounds(handle, x, y, width, height);
    }

    // 最小化・最大化中は、通常状態へ戻す矩形を更新します。
    private void ApplyRestoredBounds(ScriptLogView view,
        bool updatePlacement)
    {
        nint handle = new WindowInteropHelper(this).EnsureHandle();
        var placement = new NativePlacement
        {
            Length = Marshal.SizeOf<NativePlacement>()
        };
        if (!GetWindowPlacement(handle, ref placement))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        NativeRect current = placement.NormalPosition;
        bool workspaceCoordinates =
            (GetWindowLong(handle, ExtendedStyleIndex) & ToolWindowStyle) == 0;
        if (workspaceCoordinates)
        {
            NativePoint offset = WorkspaceOffset(
                MonitorFromWindow(handle, NearestMonitor));
            current = Offset(current, offset.X, offset.Y);
        }

        current = requestedRestoredBounds ?? current;

        int x = view.X ?? current.Left;
        int y = view.Y ?? current.Top;
        var target = new NativeRect
        {
            Left = x,
            Top = y,
            Right = checked(x + (view.Width ?? current.Right - current.Left)),
            Bottom = checked(y + (view.Height ?? current.Bottom - current.Top))
        };
        NativeRect requested = target;
        if (updatePlacement)
        {
            if (workspaceCoordinates)
            {
                NativePoint offset = WorkspaceOffset(
                    MonitorFromRect(ref target, NearestMonitor));
                target = Offset(target, checked(-offset.X), checked(-offset.Y));
            }

            placement.NormalPosition = target;
            placement.ShowCommand = HiddenShowCommand;
            if (!SetWindowPlacement(handle, ref placement))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        requestedRestoredBounds = requested;
    }

    // OSが補正した配置とは別に、明示された要求を通常状態への復元時に反映します。
    private void ApplyRequestedRestoredBounds(object? sender, EventArgs e)
    {
        // 直後の観測へ反映し、DPI処理が終わるまで要求を保持します。
        if (WindowState == WindowState.Normal
            && requestedRestoredBounds is not null)
        {
            ApplyRequestedRestoredBounds(complete: false);
            Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => ApplyRequestedRestoredBounds()));
        }
    }

    private void ApplyRequestedRestoredBounds(bool complete = true)
    {
        if (ownerClosing || WindowState != WindowState.Normal
            || requestedRestoredBounds is not NativeRect rect)
        {
            return;
        }

        nint handle = new WindowInteropHelper(this).EnsureHandle();
        if (IsIconic(handle) || IsZoomed(handle))
        {
            return;
        }

        requestedRestoredBounds = null;
        try
        {
            SetPhysicalBounds(handle, rect.Left, rect.Top,
                checked(rect.Right - rect.Left),
                checked(rect.Bottom - rect.Top));
            if (!complete)
            {
                requestedRestoredBounds = rect;
            }
        }
        catch
        {
            requestedRestoredBounds = rect;
            throw;
        }
    }

    private static NativePoint WorkspaceOffset(nint monitor)
    {
        var information = new NativeMonitorInfo
        {
            Size = Marshal.SizeOf<NativeMonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new NativePoint
        {
            X = checked(information.Work.Left - information.Monitor.Left),
            Y = checked(information.Work.Top - information.Monitor.Top)
        };
    }

    private static NativeRect Offset(NativeRect rect, int x, int y) => new()
    {
        Left = checked(rect.Left + x),
        Top = checked(rect.Top + y),
        Right = checked(rect.Right + x),
        Bottom = checked(rect.Bottom + y)
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePlacement
    {
        public int Length;
        public uint Flags;
        public uint ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window,
        ref NativePlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint window,
        ref NativePlacement placement);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor,
        ref NativeMonitorInfo information);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint window);
}
