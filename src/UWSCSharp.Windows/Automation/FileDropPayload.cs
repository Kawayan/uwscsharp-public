using System.Buffers.Binary;

namespace UWSCSharp.Windows;

/// <summary>
/// クライアント座標と絶対パスをUnicodeのDROPFILESへ格納します。
/// </summary>
internal static class FileDropPayload
{
    internal static byte[] Create(IReadOnlyList<string> paths, int x, int y,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        token.ThrowIfCancellationRequested();
        if (paths.Count == 0)
        {
            throw new ArgumentException("送信するファイルがありません。", nameof(paths));
        }

        long length = 22;
        var snapshot = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(path) || path.Contains('\0'))
            {
                throw new ArgumentException("ファイルのパスに空文字列やNULは使えません。", nameof(paths));
            }

            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("ファイルのパスには絶対パスを指定してください。", nameof(paths));
            }

            length += ((long)path.Length + 1) * 2;
            if (length > Array.MaxLength)
            {
                throw new ArgumentException("ファイル転送データが大きすぎます。", nameof(paths));
            }

            snapshot.Add(path);
        }

        token.ThrowIfCancellationRequested();
        byte[] bytes = new byte[(int)length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 20);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), x);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), y);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 1);
        int offset = 20;
        foreach (string path in snapshot)
        {
            token.ThrowIfCancellationRequested();
            foreach (char unit in path)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), unit);
                offset += 2;
            }

            offset += 2;
        }

        return bytes;
    }
}
