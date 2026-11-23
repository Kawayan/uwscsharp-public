namespace UWSCSharp.Windows;

/// <summary>
/// 指定した色深度のBMPを一時ファイル経由で保存します。
/// </summary>
internal static class BitmapFileWriter
{
    internal static void Save(PixelImage image, string path, int depth,
        CancellationToken token)
    {
        if (depth == 24)
        {
            image.SaveBmp(path, token);
            return;
        }

        BitmapEncoding.Data data = BitmapEncoding.Encode(image, depth, token);
        ImageFileWriter.Write(path, temporary =>
            WindowsOperationErrors.FileAccess(() =>
            {
                using var stream = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                using var writer = new BinaryWriter(stream);
                int offset = 14 + data.Info.Length;
                writer.Write((ushort)0x4d42);
                writer.Write(offset + data.Pixels.Length);
                writer.Write(0);
                writer.Write(offset);
                writer.Write(data.Info);
                for (int position = 0; position < data.Pixels.Length; position += 65536)
                {
                    token.ThrowIfCancellationRequested();
                    writer.Write(data.Pixels, position,
                        Math.Min(65536, data.Pixels.Length - position));
                }
            }), token);
    }
}
