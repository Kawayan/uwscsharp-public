using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class NativeTreeStateLayoutTests
{
    private static TreeControlTests.Item ReadWide(byte[] transfer)
    {
        // 原版の転送末尾52bytesを、標準構造体の56bytesへゼロ補完して解析します。
        Span<byte> item = stackalloc byte[Marshal.SizeOf<TreeControlTests.Item>()];
        item.Clear();
        transfer.AsSpan(512).CopyTo(item);
        return MemoryMarshal.Read<TreeControlTests.Item>(item);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Item32
    {
        public uint Mask;
        public uint Handle;
        public uint State;
        public uint StateMask;
        public uint Text;
        public int Capacity;
        public int Image;
        public int SelectedImage;
        public int Children;
        public uint Parameter;
    }

    [Fact]
    public void OriginalNarrowStateOffsetsMatchPointer32Layout()
    {
        Assert.Equal(4, Marshal.OffsetOf<Item32>(nameof(Item32.Handle)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<Item32>(nameof(Item32.State)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<Item32>(nameof(Item32.StateMask)).ToInt32());
        byte[] transfer = new byte[0x228];
        BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(512), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(516), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(524), 0xf000);
        Item32 request = MemoryMarshal.Read<Item32>(transfer.AsSpan(512));
        Assert.Equal(8u, request.Mask);
        Assert.Equal(0x12345678u, request.Handle);
        Assert.Equal(0xf000u, request.StateMask);
        BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(520), 0x2000);
        Assert.Equal(2u, (MemoryMarshal.Read<Item32>(transfer.AsSpan(512)).State >> 12) & 3);
    }

    [Fact]
    public void OriginalWideStateOffsetsDifferFromNativePointer64Layout()
    {
        if (IntPtr.Size != 8)
        {
            return;
        }

        Assert.Equal(8, Marshal.OffsetOf<TreeControlTests.Item>(nameof(TreeControlTests.Item.Handle)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<TreeControlTests.Item>(nameof(TreeControlTests.Item.State)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<TreeControlTests.Item>(nameof(TreeControlTests.Item.StateMask)).ToInt32());
        byte[] transfer = new byte[0x234];
        BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(512), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(520), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(528), 0xf000);
        TreeControlTests.Item request = ReadWide(transfer);
        Assert.Equal(8u, request.Mask);
        Assert.Equal((nint)0x12345678, request.Handle);
        Assert.Equal(0xf000u, request.State);
        Assert.Equal(0u, request.StateMask);
        // 標準配置の返却stateを書いても、原版の読取位置はhandle上位側です。
        BinaryPrimitives.WriteUInt32LittleEndian(transfer.AsSpan(528), 0x2000);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(transfer.AsSpan(524)));
        Assert.Equal(2u, (ReadWide(transfer).State >> 12) & 3);
    }
}
