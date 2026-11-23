using System.Buffers.Binary;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
public sealed class BmpFormatTests
{
    private sealed class OversizedStream : MemoryStream
    {
        internal OversizedStream() : base(
            [0x42, 0x4d, 0, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0])
        {
        }

        public override long Length => long.MaxValue;
    }

    [Fact]
    public void OversizedFileIsRejectedBeforeBufferAllocation()
    {
        using var stream = new OversizedStream();
        Assert.Throws<InvalidDataException>(() =>
            PixelImage.LoadBmp(stream, default));
        Assert.False(stream.CanRead);
    }

    public static IEnumerable<object[]> Formats()
    {
        foreach (object[] row in ClipboardDibTests.Formats())
        {
            yield return row;
            if ((string)row[0] == "dib-32")
            {
                foreach (int size in new[] { 52, 56 })
                {
                    byte[] original = (byte[])row[1];
                    byte[] data = [.. original[..40], .. new byte[size - 40],
                        .. original[40..]];
                    BinaryPrimitives.WriteInt32LittleEndian(data, size);
                    yield return [$"dib-header-{size}", data, row[2]];
                }
            }
        }
    }

    [Fact]
    public void HighColorOptimizationPaletteAbove256IsRejected()
    {
        object[] row = ClipboardDibTests.Formats().Single(row =>
            (string)row[0] == "dib-24");
        byte[] original = (byte[])row[1];
        byte[] dib = [.. original[..40], .. new byte[257 * 4],
            .. original[40..]];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(32), 257);
        byte[] data = new byte[14 + dib.Length];
        data[0] = 0x42;
        data[1] = 0x4d;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), 54 + 257 * 4);
        dib.CopyTo(data, 14);
        using var stream = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() =>
            PixelImage.LoadBmp(stream, default));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void FileFormatsPreservePixelsAndExplicitOffset(
        string name, byte[] dib, int[] expected)
    {
        Assert.NotEmpty(name);
        int header = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        int colors = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
        int masks = header == 40
            && BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16)) == 3
            ? 12 : 0;
        int offset = header + masks
            + (bits <= 8 && colors == 0 ? 1 << bits : colors) * 4;
        foreach (int gap in new[] { 0, 9 })
        {
            byte[] data = new byte[14 + dib.Length + gap];
            data[0] = 0x42;
            data[1] = 0x4d;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10),
                14 + offset + gap);
            dib[..offset].CopyTo(data, 14);
            dib[offset..].CopyTo(data, 14 + offset + gap);
            using var stream = new MemoryStream(data);
            Assert.Equal(expected, PixelImage.LoadBmp(stream, default).Pixels);
            Assert.False(stream.CanRead);
            for (int length = 0; length < data.Length; length++)
            {
                using var truncated = new MemoryStream(data[..length]);
                Exception? failure = Record.Exception(() =>
                    PixelImage.LoadBmp(truncated, default));
                Assert.True(failure is InvalidDataException
                    or EndOfStreamException, failure?.ToString());
                Assert.False(truncated.CanRead);
            }

            using var embedded = new MemoryStream([1, 2, 3, .. data]);
            embedded.Position = 3;
            Assert.Equal(expected,
                PixelImage.LoadBmp(embedded, default).Pixels);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(24)]
    public void CoreHeaderUsesRgbTriplesAndFileOffset(int bits)
    {
        int colors = bits == 24 ? 0 : 1 << bits;
        int offset = 26 + colors * 3 + 7;
        byte[] data = new byte[offset + 8];
        data[0] = 0x42;
        data[1] = 0x4d;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), offset);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24), (ushort)bits);
        if (colors > 0)
        {
            data[26] = 0x56;
            data[27] = 0x34;
            data[28] = 0x12;
        }
        else
        {
            data[offset] = data[offset + 4] = 0x56;
            data[offset + 1] = data[offset + 5] = 0x34;
            data[offset + 2] = data[offset + 6] = 0x12;
        }

        using var stream = new MemoryStream(data);
        Assert.Equal([0x123456, 0x123456],
            PixelImage.LoadBmp(stream, default).Pixels);
    }
}
