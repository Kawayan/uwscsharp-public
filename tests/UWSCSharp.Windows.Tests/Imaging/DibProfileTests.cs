using System.Buffers.Binary;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class DibProfileTests
{
    private static (byte[] Data, int[] Expected, int Profile) Image(bool rle,
        uint type = 0x4d424544)
    {
        object[] row = ClipboardDibTests.Formats().Single(row =>
            (string)row[0] == (rle ? "dib-rle8" : "dib-v5"));
        byte[] source = (byte[])row[1];
        byte[] body = rle
            ? [.. source[..40], .. new byte[84], .. source[40..]] : source;
        byte[] data = [.. body, .. new byte[16]];
        BinaryPrimitives.WriteInt32LittleEndian(data, 124);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), type);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(112), body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(116), 16);
        return (data, (int[])row[2], body.Length);
    }

    [Theory]
    [InlineData(false, 0x4d424544u)]
    [InlineData(true, 0x4d424544u)]
    [InlineData(false, 0x4c494e4bu)]
    [InlineData(true, 0x4c494e4bu)]
    public void ProfileMetadataDoesNotChangeEncodedRgb(bool rle, uint type)
    {
        (byte[] data, int[] expected, int profile) = Image(rle, type);
        if (type == 0x4c494e4b)
        {
            "missing.icm\0"u8.CopyTo(data.AsSpan(profile));
        }

        Assert.Equal(expected, ClipboardDib.Decode(data, default).Pixels);
        Assert.Throws<InvalidDataException>(() =>
            ClipboardDib.Decode(data[..^1], default));
    }

    [Theory]
    [InlineData(112, 0u)]
    [InlineData(112, 139u)]
    [InlineData(112, uint.MaxValue)]
    [InlineData(116, 0u)]
    [InlineData(116, uint.MaxValue)]
    public void ProfileBoundsAndPixelOverlapAreRejected(int offset,
        uint value)
    {
        (byte[] data, _, _) = Image(false);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() =>
            ClipboardDib.Decode(data, default));
    }

    [Fact]
    public void TruncatedRleCannotUseProfileBytesAsItsTerminator()
    {
        (byte[] data, _, int profile) = Image(true);
        data[profile - 1] = 0;
        data[profile] = 0;
        data[profile + 1] = 1;
        Assert.Throws<InvalidDataException>(() =>
            ClipboardDib.Decode(data, default));
    }

    [Fact]
    public void RleCannotReadPastItsDeclaredPayloadLength()
    {
        object[] row = ClipboardDibTests.Formats().Single(row =>
            (string)row[0] == "dib-rle8");
        byte[] data = (byte[])row[1];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20),
            data.Length - 56 - 1);
        Assert.Throws<InvalidDataException>(() =>
            ClipboardDib.Decode(data, default));
    }
}
