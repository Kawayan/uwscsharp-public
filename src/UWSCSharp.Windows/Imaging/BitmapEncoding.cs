using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace UWSCSharp.Windows;

/// <summary>
/// GDIの標準色表を使い、指定した色深度へ変換します。
/// </summary>
internal static class BitmapEncoding
{
    internal sealed record Data(byte[] Info, byte[] Pixels)
    {
        internal byte[] PackedDib()
        {
            byte[] data = new byte[Info.Length + Pixels.Length];
            Info.CopyTo(data, 0);
            Pixels.CopyTo(data, Info.Length);
            return data;
        }
    }

    internal static Data Encode(PixelImage image, int depth,
        CancellationToken token, BitmapConversionApi? native = null)
    {
        token.ThrowIfCancellationRequested();
        if (depth is not (1 or 4 or 8 or 16 or 24 or 32))
        {
            throw WindowsOperationErrors.Record(
                new ArgumentOutOfRangeException(nameof(depth)));
        }

        BitmapConversionApi api = native ?? new BitmapConversionApi();
        nint dc = 0;
        nint bitmap = 0;
        try
        {
            dc = api.GetDC();
            RequireNative(dc != 0);
            var pin = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
            try
            {
                bitmap = api.CreateBitmap(dc, image, pin.AddrOfPinnedObject());
                RequireNative(bitmap != 0);
            }
            finally
            {
                pin.Free();
            }

            token.ThrowIfCancellationRequested();
            int stride = checked((image.Width * depth + 31) / 32 * 4);
            byte[] pixels = new byte[checked(stride * image.Height)];
            // 色表256件とRGBマスクのどちらにも十分な領域を確保します。
            byte[] info = new byte[40 + 256 * 4 + 12];
            BinaryPrimitives.WriteInt32LittleEndian(info, 40);
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(4), image.Width);
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(8), image.Height);
            BinaryPrimitives.WriteInt16LittleEndian(info.AsSpan(12), 1);
            BinaryPrimitives.WriteInt16LittleEndian(info.AsSpan(14), (short)depth);
            RequireNative(api.ReadBitmap(dc, bitmap, image.Height,
                pixels, info) == image.Height);
            token.ThrowIfCancellationRequested();
            int compression = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(16));
            uint colors = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(32));
            if (BinaryPrimitives.ReadInt32LittleEndian(info) != 40
                || BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(4)) != image.Width
                || BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(8)) != image.Height
                || BinaryPrimitives.ReadInt16LittleEndian(info.AsSpan(12)) != 1
                || BinaryPrimitives.ReadInt16LittleEndian(info.AsSpan(14)) != depth
                || compression is not (0 or 3)
                || (compression == 3 && depth is not (16 or 32))
                || colors > (depth <= 8 ? 1u << depth : 0))
            {
                throw WindowsOperationErrors.Record(
                    new InvalidDataException("変換後のBMP形式が不正です。"));
            }

            int count = depth <= 8 ? (colors == 0 ? 1 << depth : (int)colors) : 0;
            int infoLength = 40 + count * 4 + (compression == 3 ? 12 : 0);
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(20), pixels.Length);
            for (int index = 0; index < count; index++)
            {
                info[40 + index * 4 + 3] = 0;
            }

            int rowBytes = (image.Width * depth + 7) / 8;
            int usedBits = image.Width * depth % 8;
            for (int y = 0; y < image.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                Array.Clear(pixels, y * stride + rowBytes, stride - rowBytes);
                if (usedBits != 0)
                {
                    pixels[y * stride + rowBytes - 1] &= (byte)(0xff << (8 - usedBits));
                }

                if (depth == 32)
                {
                    for (int x = 0; x < image.Width; x++)
                    {
                        pixels[y * stride + x * 4 + 3] = 0;
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            return new(info[..infoLength], pixels);
        }
        finally
        {
            try
            {
                if (bitmap != 0)
                {
                    api.DeleteBitmap(bitmap);
                }
            }
            finally
            {
                if (dc != 0)
                {
                    api.ReleaseDC(dc);
                }
            }
        }
    }

    private static void RequireNative(bool success)
    {
        if (!success)
        {
            throw WindowsOperationErrors.Record(
                new Win32Exception(Marshal.GetLastWin32Error(),
                    "BMPの色深度を変換できませんでした。"));
        }
    }
}
