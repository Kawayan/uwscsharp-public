using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 共有カーソルを借用して現在の種類を取得します。
/// </summary>
internal static partial class CursorReader
{
    internal static readonly (int Kind, int Resource)[] StandardCursors =
    [
        (1, 32650), (2, 32512), (3, 32515), (4, 32649),
        (5, 32651), (6, 32513), (8, 32648), (10, 32646),
        (11, 32643), (12, 32645), (13, 32642), (14, 32644),
        (15, 32516), (16, 32514)
    ];

    internal static double Read()
    {
        var info = new CursorInfo
        {
            Size = Marshal.SizeOf<CursorInfo>()
        };
        if (!GetCursorInfo(ref info))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return Classify(info.Handle, resource => Native.LoadCursorW(0, resource));
    }

    internal static double Classify(nint handle, Func<int, nint> load)
    {
        if (handle == 0)
        {
            return 0;
        }

        foreach ((int kind, int resource) in StandardCursors)
        {
            nint standard = load(resource);
            if (standard == 0)
            {
                throw WindowsOperationErrors.Record(
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (handle == standard)
            {
                return kind;
            }
        }

        // ハンドルは借用するだけで、共有カーソルを破棄しません。
        return -(double)(nuint)handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        internal int Size;
        internal uint Flags;
        internal nint Handle;
        internal Native.Point Position;
    }
}
