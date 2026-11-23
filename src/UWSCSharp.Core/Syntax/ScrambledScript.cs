using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace UWSCSharp.Core;

internal static class ScrambledScript
{
    internal const int SeedCount = 9;
    private const int SignatureLength = 12;

    private static ReadOnlySpan<byte> Offsets =>
    [
        3, 5, 0, 9, 1, 7, 2, 6, 4, 1, 9, 3, 0, 8, 7, 2,
        5, 7, 1, 8, 4, 6, 0, 5, 1, 8, 3, 6, 9, 1, 2, 4
    ];

    internal static bool HasSignature(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(
            (ReadOnlySpan<byte>)[0xff, 0xfe, 0x55, 0, 0x57, 0, 0x53, 0, 0x30, 0, 0x35, 0]);

    internal static byte[] EncodeVerified(string source, int firstSeed)
    {
        // 本文の変換結果が置換記号と重なる場合は、別の初期位置を試します。
        for (int attempt = 0; attempt < SeedCount; attempt++)
        {
            byte[] bytes = Encode(source, (firstSeed + attempt) % SeedCount);
            using var expected = new StringReader(source);
            using var actual = new StringReader(Decode(bytes));
            while (true)
            {
                string? line = expected.ReadLine();
                if (!string.Equals(line, actual.ReadLine(), StringComparison.Ordinal))
                {
                    break;
                }

                if (line is null)
                {
                    return bytes;
                }
            }
        }

        throw new IOException("UWS05の置換記号と本文がすべての初期位置で衝突しました。");
    }

    internal static byte[] Encode(string source, int seed)
    {
        var body = new StringBuilder(source.Length);
        ushort checksum = 0;
        int position = seed;
        int row = 0;
        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } line)
        {
            int column = 1;
            foreach (char value in line)
            {
                int offset = Offsets[position] + 1 + (row + column) % 5;
                char encoded = unchecked((char)((value ^ (0x5555 + row)) + offset));
                string? escape = encoded switch
                {
                    '\0' => "_<#00>_",
                    '\n' => "_<#10>_",
                    '\r' => "_<#13>_",
                    '\x1a' => "_<#26>_",
                    _ => null
                };

                if (escape is null)
                {
                    body.Append(encoded);
                    checksum = unchecked((ushort)(checksum + (encoded & 0xff)));
                }
                else
                {
                    body.Append(escape);
                    foreach (char symbol in escape)
                    {
                        checksum = unchecked((ushort)(checksum + symbol));
                    }
                }

                position = (position + 1) % Offsets.Length;
                column = (column + 1) % 5;
            }

            body.Append("\r\n");
            row = (row + 1) % 5;
        }

        string header = string.Create(CultureInfo.InvariantCulture,
            $"\ufeffUWS05\r\n{checksum}{seed}\r\n");
        byte[] bytes = new byte[checked((header.Length + body.Length) * 2)];
        int index = 0;
        // 単独サロゲートを置換せず、UTF-16コード単位をそのまま保存します。
        foreach (char value in header)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index, 2), value);
            index += 2;
        }

        foreach (ReadOnlyMemory<char> chunk in body.GetChunks())
        {
            foreach (char value in chunk.Span)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index, 2), value);
                index += 2;
            }
        }

        return bytes;
    }

    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length % 2 != 0)
        {
            throw InvalidFormat();
        }

        // 変換後のコード単位は単独サロゲートになる場合もあるため、
        // 文字コードのデコーダーを通さず、そのまま読み取ります。
        string payload = string.Create((bytes.Length - SignatureLength) / 2,
            bytes, static (characters, input) =>
            {
                for (int index = 0; index < characters.Length; index++)
                {
                    characters[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(
                        input.AsSpan(SignatureLength + index * 2, 2));
                }
            });

        if (!payload.StartsWith("\r\n", StringComparison.Ordinal))
        {
            throw InvalidFormat();
        }

        ReadOnlySpan<char> remaining = payload.AsSpan(2);
        int headerEnd = remaining.IndexOf("\r\n", StringComparison.Ordinal);
        if (headerEnd < 2
            || remaining[..headerEnd].IndexOfAnyExceptInRange('0', '9') >= 0
            || !ushort.TryParse(remaining[..(headerEnd - 1)], NumberStyles.None,
                CultureInfo.InvariantCulture, out ushort expected))
        {
            throw InvalidFormat();
        }

        int position = remaining[headerEnd - 1] - '0';
        remaining = remaining[(headerEnd + 2)..];
        var source = new StringBuilder(remaining.Length);
        ushort checksum = 0;
        int row = 0;
        while (!remaining.IsEmpty)
        {
            int end = remaining.IndexOf("\r\n", StringComparison.Ordinal);
            if (end < 0)
            {
                throw InvalidFormat();
            }

            ReadOnlySpan<char> line = remaining[..end];
            foreach (char value in line)
            {
                // 行区切り以外の制御文字は、ファイル上では置換記号になります。
                if (value is '\0' or '\n' or '\r' or '\x1a')
                {
                    throw InvalidFormat();
                }

                checksum = unchecked((ushort)(checksum + (value & 0xff)));
            }

            int column = 1;
            for (int index = 0; index < line.Length; index++)
            {
                char value = line[index];
                if (value == '_' && line.Length - index >= 7
                    && ReadEscape(line.Slice(index, 7)) is char escaped)
                {
                    value = escaped;
                    index += 6;
                }

                int offset = Offsets[position] + 1 + (row + column) % 5;
                ushort decoded = unchecked((ushort)(value - offset));
                source.Append((char)(decoded ^ (0x5555 + row)));
                position = (position + 1) % Offsets.Length;
                column = (column + 1) % 5;
            }

            source.Append("\r\n");
            row = (row + 1) % 5;
            remaining = remaining[(end + 2)..];
        }

        if (checksum != expected)
        {
            throw new IOException("UWS05の検査値が本文と一致しません。");
        }

        return source.ToString();
    }

    private static char? ReadEscape(ReadOnlySpan<char> text) => text switch
    {
        "_<#00>_" => '\0',
        "_<#10>_" => '\n',
        "_<#13>_" => '\r',
        "_<#26>_" => '\x1a',
        _ => null
    };

    private static IOException InvalidFormat() =>
        new("UWS05のファイル形式が不正です。");
}
