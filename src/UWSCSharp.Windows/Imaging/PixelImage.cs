namespace UWSCSharp.Windows;

internal sealed class PixelImage
{
    internal const int MaximumPixels = 16777216;

    internal int Width
    {
        get;
    }

    internal int Height
    {
        get;
    }

    internal int[] Pixels
    {
        get;
    }

    internal PixelImage(int width, int height, int[] pixels)
    {
        if (width < 1 || height < 1
            || (long)width * height > MaximumPixels
            || (long)width * height != pixels.Length)
        {
            throw WindowsOperationErrors.Record(
                new ArgumentException("画像の寸法が範囲外です。"));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    internal int this[int x, int y] => Pixels[y * Width + x] & 0xffffff;

    internal static PixelImage LoadBmp(string path, CancellationToken token) =>
        WindowsOperationErrors.FileAccess(() =>
        {
            using var stream = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            return LoadBmp(stream, token);
        });

    internal static PixelImage LoadBmp(Stream stream, CancellationToken token)
    {
        try
        {
            return ReadBmp(stream, token);
        }
        catch (EndOfStreamException failure)
        {
            WindowsOperationErrors.Record(failure);
            throw;
        }
    }

    private static PixelImage ReadBmp(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0x4d42)
        {
            throw WindowsOperationErrors.Record(
                new InvalidDataException("BMP形式ではありません。"));
        }

        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        uint offset = reader.ReadUInt32();
        long length = stream.Length - stream.Position;
        if (length < 0 || length > 134217728
            || offset < 14 || offset > length + 14)
        {
            throw WindowsOperationErrors.Record(
                new InvalidDataException("BMPのデータサイズまたは画素位置が範囲外です。"));
        }

        byte[] data = new byte[(int)length];
        for (int position = 0; position < data.Length; position += 65536)
        {
            token.ThrowIfCancellationRequested();
            stream.ReadExactly(data.AsSpan(position,
                Math.Min(65536, data.Length - position)));
        }

        token.ThrowIfCancellationRequested();
        return DibDecoder.Decode(data, token, checked((int)offset - 14));
    }

    internal void SaveBmp(string path, CancellationToken token)
    {
        ImageFileWriter.Write(path, temporary => WindowsOperationErrors.FileAccess(() =>
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                int stride = (Width * 3 + 3) & ~3;
                int byteCount = stride * Height;
                writer.Write((ushort)0x4d42);
                writer.Write(54 + byteCount);
                writer.Write(0);
                writer.Write(54);
                writer.Write(40);
                writer.Write(Width);
                writer.Write(Height);
                writer.Write((ushort)1);
                writer.Write((ushort)24);
                writer.Write(0);
                writer.Write(byteCount);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                byte[] row = new byte[stride];
                for (int y = Height - 1; y >= 0; y--)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < Width; x++)
                    {
                        int color = this[x, y];
                        row[x * 3] = (byte)color;
                        row[x * 3 + 1] = (byte)(color >> 8);
                        row[x * 3 + 2] = (byte)(color >> 16);
                    }

                    writer.Write(row);
                }
            }

        }), token);
    }
}
