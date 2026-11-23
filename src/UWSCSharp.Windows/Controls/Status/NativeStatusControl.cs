using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal static class NativeStatusControl
{
    internal static Task<Native.Rect> ReadPartBoundsAsync(nint target,
        int part, CancellationToken token)
    {
        return Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId()
            ? Task.FromResult(ReadPartBounds(target, part, token))
            : NativeControlWorker.RunAsync(active => ReadPartBounds(target, part, active), token);
    }

    private static Native.Rect ReadPartBounds(nint target,
        int part, CancellationToken token)
    {
        if (part is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(part));
        }

        using var dpi = new PhysicalDpiScope();
        using var buffer = new NativeControlTextBuffer(target, ControlTextKind.Status);
        if (Message(target, 0x40a, part - 1, buffer.TextAddress, token) == 0)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("STATUSBARのpart矩形を取得できません。"));
        }

        byte[] bytes = buffer.ReadTextPrefix(16);
        var start = new Native.Point(BinaryPrimitives.ReadInt32LittleEndian(bytes),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        var end = new Native.Point(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)));
        if (!Native.ClientToScreen(target, ref start)
            || !Native.ClientToScreen(target, ref end))
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        token.ThrowIfCancellationRequested();
        return new Native.Rect
        {
            Left = start.X,
            Top = start.Y,
            Right = end.X,
            Bottom = end.Y
        };
    }

    internal static Task<(int Count, List<string> Parts)> ReadAsync(nint target,
        CancellationToken token, long part = 0, Action<string>? onItem = null)
    {
        if (Native.GetWindowThreadProcessId(target, out _) == Native.GetCurrentThreadId())
        {
            return Task.FromResult(Read(target, part, token, onItem));
        }

        return NativeControlWorker.RunAsync(active => Read(target, part, active, onItem), token);
    }

    private static nint Message(nint target, uint message, nint index,
        nint pointer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint result = Native.SendMessageW(target, message, index, pointer);
        token.ThrowIfCancellationRequested();
        if (!Native.IsWindow(target))
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("STATUSBARが取得中に破棄されました。"));
        }

        return result;
    }

    private static void CheckType(nint flags)
    {
        if (((long)flags & 0x10000000) != 0)
        {
            throw WindowsOperationErrors.Record(
                new NotSupportedException("owner drawのSTATUSBARは未対応です。"));
        }
    }

    private static (int Count, List<string> Parts) Read(nint target,
        long part, CancellationToken token, Action<string>? onItem = null)
    {
        nint count = Message(target, 0x406, 0, 0, token);
        if (count < 0 || count > 256)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("STATUSBARのpart数が範囲外です。"));
        }

        List<string> parts = [];
        if (count == 0 || part > count)
        {
            return ((int)count, parts);
        }

        // SB_GETTEXTは長さを指定できないため、最大65535文字と終端を確保します。
        using var buffer = new NativeControlTextBuffer(target, ControlTextKind.Status);
        int start = part == 0 ? 0 : checked((int)part - 1);
        int end = part == 0 ? (int)count : start + 1;
        for (int index = start; index < end; index++)
        {
            CheckType(Message(target, 0x40c, index, 0, token));
            buffer.Prepare();
            nint copied = Message(target, 0x40d, index, buffer.TextAddress, token);
            CheckType(Message(target, 0x40c, index, 0, token));
            int length = (int)((long)copied & 0xffff);
            string text = NativeListControl.Trim(buffer.Read(length + 1));
            parts.Add(text);
            onItem?.Invoke(text);
        }

        if (Message(target, 0x406, 0, 0, token) != count)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("STATUSBARのpart数が取得中に変化しました。"));
        }

        return ((int)count, parts);
    }
}
