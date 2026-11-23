using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

internal sealed class NativeTreeClickBuffer(NativeControlBufferOwner owner)
{
    private byte[]? transfer;
    private readonly byte[] rectangle = new byte[16];

    internal nint Root => owner.Root;

    internal uint MatchFlags
    {
        get;
        private set;
    }

    internal Native.Rect ReadRectangle(nint target, nint item,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Native.SendMessageW(target, 0x1114, 0, item);
        token.ThrowIfCancellationRequested();
        nint address = owner.Acquire().Address;
        byte[] handle = new byte[owner.Is64Bit ? 8 : 4];
        if (owner.Is64Bit)
        {
            // 原版のDWORD転送との差を残し、対象のhandle幅を維持します。
            BinaryPrimitives.WriteInt64LittleEndian(handle, (long)item);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(handle, unchecked((uint)item));
        }

        owner.WriteRetainingBytes(handle);
        Native.SendMessageW(target, 0x1104, 1, address);
        token.ThrowIfCancellationRequested();
        owner.ReadRetainingBytes(rectangle);
        token.ThrowIfCancellationRequested();
        return new Native.Rect
        {
            Left = BinaryPrimitives.ReadInt32LittleEndian(rectangle),
            Top = BinaryPrimitives.ReadInt32LittleEndian(rectangle.AsSpan(4)),
            Right = BinaryPrimitives.ReadInt32LittleEndian(rectangle.AsSpan(8)),
            Bottom = BinaryPrimitives.ReadInt32LittleEndian(rectangle.AsSpan(12)),
        };
    }

    internal void Begin()
    {
        bool wide = owner.Is64Bit;
        transfer ??= new byte[wide ? 0x234 : 0x228];
    }

    internal int ReadStateImage(nint target, nint item,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Begin();
        nint address = owner.Acquire().Address;
        bool wide = owner.Is64Bit;
        byte[] bytes = transfer!;
        Array.Clear(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(512), 8);
        if (wide)
        {
            // 原版のDWORD配置との差を残し、対象の標準構造体とhandle幅を維持します。
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(520), (long)item);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(516), unchecked((uint)item));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(wide ? 532 : 524), 0xf000);
        owner.WriteRetainingBytes(bytes);
        Native.SendMessageW(target, 0x113e, 0, address + 512);
        token.ThrowIfCancellationRequested();
        owner.ReadRetainingBytes(bytes);
        token.ThrowIfCancellationRequested();
        uint state = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(wide ? 528 : 520));
        return (int)((state >> 12) & 3);
    }

    internal (string Name, int Children) ReadItem(nint target, nint item,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Begin();
        nint address = owner.Acquire().Address;
        bool wide = owner.Is64Bit;
        byte[] bytes = transfer!;
        Array.Clear(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(512), 0x41);
        if (wide)
        {
            // 64bit対象のhandleを切り詰めず保持します。
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(520), (long)item);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(536), (long)address);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(516), unchecked((uint)item));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(528), unchecked((uint)address));
        }

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(wide ? 544 : 532), 256);
        owner.WriteRetainingBytes(bytes);
        Native.SendMessageW(target, 0x113e, 0, address + 512);
        token.ThrowIfCancellationRequested();
        owner.ReadRetainingBytes(bytes);
        token.ThrowIfCancellationRequested();
        ReadOnlySpan<char> text = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, 512));
        int end = text.IndexOf('\0');
        string name = NativeListControl.Trim(new string(end < 0 ? text : text[..end]));
        int children = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(wide ? 556 : 544));
        // 原版の読取位置を維持します。64bit側は標準state位置とは異なります。
        MatchFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(wide ? 524 : 520));
        return (name, children);
    }
}
