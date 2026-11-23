using System.Buffers.Binary;
using System.Numerics;

namespace UWSCSharp.Windows;

/// <summary>
/// DIBの色表・向き・圧縮形式を検査して読み取ります。
/// </summary>
internal static class DibDecoder
{
    internal static PixelImage Decode(byte[] data, CancellationToken token,
        int? pixelOffset = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            return Read(data, token, pixelOffset);
        }
        catch (Exception error) when (error is InvalidDataException
            or NotSupportedException)
        {
            WindowsOperationErrors.Record(error);
            throw;
        }
    }

    private static PixelImage Read(byte[] data, CancellationToken token,
        int? pixelOffset)
    {
        Require(data.Length >= 4);
        int size = I32(data, 0);
        if (size == 12)
        {
            return ReadCore(data, token, pixelOffset);
        }

        Require(data.Length >= 40);
        Require(size is 40 or 52 or 56 or 108 or 124
            && data.Length >= size);
        int width = I32(data, 4);
        int signedHeight = I32(data, 8);
        Require(width > 0 && signedHeight != 0
            && signedHeight != int.MinValue);
        int height = Math.Abs(signedHeight);
        Require((long)width * height <= PixelImage.MaximumPixels);
        int bits = U16(data, 14);
        int compression = I32(data, 16);
        Require(U16(data, 12) == 1);
        if (bits is not (1 or 4 or 8 or 16 or 24 or 32)
            || compression is not (0 or 1 or 2 or 3)
            || (compression == 1 && bits != 8)
            || (compression == 2 && bits != 4)
            || (compression == 3 && bits is not (16 or 32))
            || (compression is 1 or 2 && signedHeight < 0))
        {
            throw new NotSupportedException("未対応のDIB圧縮形式です。");
        }

        int pixelLimit = data.Length;
        if (size == 124 && U32(data, 56) is 0x4c494e4b or 0x4d424544)
        {
            uint profile = U32(data, 112);
            uint length = U32(data, 116);
            Require(profile > 0 && length > 0
                && (long)profile + length <= data.Length);
            // 色変換やリンク先の読込みはせず、RGBと補助領域を区別します。
            pixelLimit = (int)profile;
        }

        int offset = size;
        uint[] masks = bits == 16
            ? [0x7c00, 0x03e0, 0x001f] : [0xff0000, 0xff00, 0xff];
        if (compression == 3)
        {
            Require(data.Length >= 52);
            masks = [U32(data, 40), U32(data, 44), U32(data, 48)];
            if (size == 40)
            {
                offset += 12;
            }

            uint used = 0;
            foreach (uint mask in masks)
            {
                Require(mask != 0 && (mask & used) == 0
                    && (bits == 32 || mask <= 0xffff));
                ulong normalized = mask >> BitOperations.TrailingZeroCount(mask);
                Require((normalized & (normalized + 1)) == 0);
                used |= mask;
            }
        }

        uint colors = U32(data, 32);
        Require(colors <= (bits <= 8 ? 1u << bits : 256u));
        int count = bits <= 8 && colors == 0 ? 1 << bits : (int)colors;
        Require((long)offset + count * 4 <= data.Length);
        int[] palette = new int[count];
        for (int index = 0; index < count; index++)
        {
            palette[index] = I32(data, offset + index * 4) & 0xffffff;
        }

        offset += count * 4;
        if (pixelOffset is int explicitOffset)
        {
            Require(explicitOffset >= offset && explicitOffset <= data.Length);
            offset = explicitOffset;
        }

        Require(pixelLimit >= offset);
        int[] pixels = new int[width * height];
        if (compression is 1 or 2)
        {
            uint encodedSize = U32(data, 20);
            Require(encodedSize > 0 && encodedSize <= pixelLimit - offset);
            ReadRle(data.AsSpan(0, offset + (int)encodedSize), offset,
                width, height, bits, palette, pixels, token);
            return new(width, height, pixels);
        }

        long stride = ((long)width * bits + 31) / 32 * 4;
        Require(stride * height <= pixelLimit - offset);
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            int row = offset + (int)stride * (signedHeight < 0 ? y : height - y - 1);
            for (int x = 0; x < width; x++)
            {
                int value;
                if (bits <= 8)
                {
                    int index = (data[row + x * bits / 8]
                        >> (8 - bits - x * bits % 8)) & ((1 << bits) - 1);
                    Require(index < palette.Length);
                    value = palette[index];
                }
                else
                {
                    int position = row + x * (bits / 8);
                    uint raw = bits == 16 ? U16(data, position)
                        : bits == 32 ? U32(data, position)
                        : (uint)(data[position] | data[position + 1] << 8
                            | data[position + 2] << 16);
                    value = Channel(raw, masks[0]) << 16
                        | Channel(raw, masks[1]) << 8 | Channel(raw, masks[2]);
                }

                pixels[y * width + x] = value;
            }
        }

        token.ThrowIfCancellationRequested();
        return new(width, height, pixels);
    }

    private static PixelImage ReadCore(byte[] data, CancellationToken token,
        int? pixelOffset)
    {
        Require(data.Length >= 12 && U16(data, 8) == 1);
        int width = U16(data, 4);
        int height = U16(data, 6);
        int bits = U16(data, 10);
        Require(width > 0 && height > 0
            && (long)width * height <= PixelImage.MaximumPixels);
        if (bits is not (1 or 4 or 8 or 24))
        {
            throw new NotSupportedException("未対応のCORE BMP色深度です。");
        }

        int colors = bits == 24 ? 0 : 1 << bits;
        int offset = pixelOffset ?? 12 + colors * 3;
        long stride = ((long)width * bits + 31) / 32 * 4;
        Require(offset >= 12 + colors * 3 && offset <= data.Length
            && stride * height <= data.Length - offset);
        byte[] normalized = new byte[40 + colors * 4 + (int)stride * height];
        BinaryPrimitives.WriteInt32LittleEndian(normalized, 40);
        BinaryPrimitives.WriteInt32LittleEndian(normalized.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(normalized.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(normalized.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(normalized.AsSpan(14), (ushort)bits);
        for (int index = 0; index < colors; index++)
        {
            data.AsSpan(12 + index * 3, 3)
                .CopyTo(normalized.AsSpan(40 + index * 4));
        }

        data.AsSpan(offset, (int)stride * height)
            .CopyTo(normalized.AsSpan(40 + colors * 4));
        return Read(normalized, token, null);
    }

    private static int Channel(uint value, uint mask)
    {
        int shift = BitOperations.TrailingZeroCount(mask);
        return (int)(((ulong)((value & mask) >> shift) * 255)
            / (mask >> shift));
    }

    private static void ReadRle(ReadOnlySpan<byte> data, int offset, int width,
        int height, int bits, int[] palette, int[] pixels,
        CancellationToken token)
    {
        Require(palette.Length > 0);
        Array.Fill(pixels, palette[0]);
        int x = 0;
        int y = height - 1;
        while (offset < data.Length)
        {
            token.ThrowIfCancellationRequested();
            Require(offset + 2 <= data.Length);
            int count = data[offset++];
            int value = data[offset++];
            if (count == 0 && value == 1)
            {
                return;
            }

            if (count == 0 && value == 0)
            {
                x = 0;
                y--;
                continue;
            }

            if (count == 0 && value == 2)
            {
                Require(offset + 2 <= data.Length);
                x += data[offset++];
                y -= data[offset++];
                Require(x <= width && y >= 0);
                continue;
            }

            bool literal = count == 0;
            int length = literal ? value : count;
            int bytes = (length * bits + 7) / 8;
            Require(y >= 0 && x + length <= width
                && (!literal || offset + bytes + (bytes & 1) <= data.Length));
            for (int index = 0; index < length; index++)
            {
                int packed = literal ? data[offset + index * bits / 8] : value;
                int color = bits == 8 ? packed
                    : (packed >> (index % 2 == 0 ? 4 : 0)) & 15;
                Require(color < palette.Length);
                pixels[y * width + x++] = palette[color];
            }

            if (literal)
            {
                offset += bytes + (bytes & 1);
            }
        }

        throw new InvalidDataException("DIBのRLE終端がありません。");
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException("DIBのヘッダーまたは画素領域が不正です。");
        }
    }

    private static int I32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
}
