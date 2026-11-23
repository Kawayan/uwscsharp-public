using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed class NativeTabClickBuffer(NativeControlBufferOwner owner)
{
    internal IEnumerable<(int Index, string Name)> ReadNames(nint target,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint address = owner.Acquire().Address;
        bool wide = owner.Is64Bit;
        byte[] bytes = new byte[wide ? 0x224 : 0x21c];
        int count = WindowsOperationErrors.Int32(Native.SendMessageW(target, 0x1304, 0, 0));
        _ = WindowsOperationErrors.Subtract(count, 1);
        if (count > UwscArray.MaximumElements)
        {
            throw WindowsOperationErrors.Record(
                new InvalidOperationException("TABの項目数が範囲外です。"));
        }

        for (int index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(512), 1);
            if (wide)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(528), (long)address);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(524), unchecked((uint)address));
            }

            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(wide ? 536 : 528), 256);
            owner.WriteRetainingBytes(bytes);
            Native.SendMessageW(target, 0x133c, index, address + 512);
            token.ThrowIfCancellationRequested();
            owner.ReadRetainingBytes(bytes);
            token.ThrowIfCancellationRequested();
            yield return (index, Name(bytes));
        }
    }

    internal Native.Rect ReadRectangle(nint target, int index,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        nint address = owner.Acquire().Address;
        _ = Native.SendMessageW(target, 0x130a, index, address);
        token.ThrowIfCancellationRequested();
        byte[] bytes = new byte[16];
        owner.ReadRetainingBytes(bytes);
        token.ThrowIfCancellationRequested();
        return new Native.Rect
        {
            Left = BinaryPrimitives.ReadInt32LittleEndian(bytes),
            Top = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)),
            Right = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)),
            Bottom = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)),
        };
    }

    private static string Name(byte[] bytes)
    {
        ReadOnlySpan<char> text = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, 512));
        int end = text.IndexOf('\0');
        return ButtonName.Normalize(new string(end < 0 ? text : text[..end]));
    }
}
