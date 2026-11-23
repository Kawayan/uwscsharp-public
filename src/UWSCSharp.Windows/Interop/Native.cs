using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

internal static partial class Native
{
    internal const int GwlStyle = -16;
    internal const int WsVisible = 0x10000000;
    internal const int WsChild = 0x40000000;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint WmGetText = 0x000d;
    internal const uint WmGetTextLength = 0x000e;
    internal const uint WmHScroll = 0x0114;
    internal const uint WmVScroll = 0x0115;
    internal const uint BmGetCheck = 0x00f0;
    internal const uint TbmGetPos = 0x0400;
    internal const uint TbmGetRangeMin = 0x0401;
    internal const uint TbmGetRangeMax = 0x0402;
    internal const uint TbmSetPos = 0x0405;
    internal const uint SmtoAbortIfHung = 2;
    internal const uint MessageTimeoutMilliseconds = 1000;

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLongW(nint window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ScreenToClient(nint window,
        ref Point point);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AttachThreadInput(uint current,
        uint target, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetKeyboardState(
        [Out] byte[] state);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetKeyboardState(byte[] state);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint window,
        uint attribute, out Rect rect, int size);

    internal delegate bool MonitorCallback(nint monitor,
        nint dc, ref Rect rect, nint data);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(
        nint window, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(nint dc,
        nint clip, MonitorCallback callback, nint data);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint access,
        bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsWow64Process2(
        nint process, out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageNameW(
        nint process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(
        uint extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(nint window);

    internal delegate bool EnumCallback(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public Rect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nuint Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumWindows(
        EnumCallback callback, nint data);

    [DllImport("user32.dll")]
    internal static extern bool EnumChildWindows(
        nint parent, EnumCallback callback, nint data);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowEnabled(nint window);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    internal static extern nint GetParent(nint window);

    [DllImport("user32.dll")]
    internal static extern int GetDlgCtrlID(nint window);

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint window,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    internal static extern bool IsChild(nint parent, nint child);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint window,
        StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(nint window,
        StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RealGetWindowClassW(nint window,
        StringBuilder text, uint count);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(nint window,
        out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetClientRect(nint window,
        out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ClientToScreen(nint window,
        ref Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicalCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPhysicalCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint window,
        int command);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(nint window,
        nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(nint window,
        uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(
        nint window, out uint process);

    [DllImport("user32.dll")]
    internal static extern bool GetGUIThreadInfo(
        uint thread, ref GuiThreadInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SendMessageTimeoutW(
        nint window, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint SendText(nint window,
        uint message, nint wParam, string text, uint flags,
        uint timeout, out nuint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint ReadText(nint window,
        uint message, nint wParam, StringBuilder text, uint flags,
        uint timeout, out nuint result);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKeyW(uint code,
        uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count,
        Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetClipboardData(
        uint format, nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalAlloc(uint flags,
        nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalFree(nint memory);
}
