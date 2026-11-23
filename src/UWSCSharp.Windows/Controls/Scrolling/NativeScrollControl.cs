using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static partial class NativeScrollControl
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ScrollInfo
    {
        internal uint Size;
        internal uint Mask;
        internal int Minimum;
        internal int Maximum;
        internal uint Page;
        internal int Position;
        internal int TrackPosition;
    }

    internal static ScrollInfo Read(nint target, CancellationToken token,
        int bar = 2)
    {
        token.ThrowIfCancellationRequested();
        var info = new ScrollInfo { Size = 28, Mask = 0x17 };
        if (!GetScrollInfo(target, bar, ref info))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        token.ThrowIfCancellationRequested();
        return info;
    }

    internal static void Write(nint target, int value, CancellationToken token,
        int bar = 2)
    {
        token.ThrowIfCancellationRequested();
        var info = new ScrollInfo { Size = 28, Mask = 4, Position = value };
        _ = SetScrollInfo(target, bar, ref info, true);
        token.ThrowIfCancellationRequested();
    }
}
