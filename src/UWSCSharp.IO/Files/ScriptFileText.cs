using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.IO;

/// <summary>
/// ファイルとINIで使うテキスト変換を行います。
/// </summary>
internal static class ScriptFileText
{
    internal static async Task<(List<string> Lines, Encoding Encoding)> ReadOpenLinesAsync(
        FileStream stream, CancellationToken token)
    {
        byte[] prefix = new byte[3];
        int count = 0;
        while (count < prefix.Length)
        {
            int read = await stream.ReadAsync(prefix.AsMemory(count), token)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        int skip = 0;
        Encoding encoding = new UTF8Encoding(false, true);
        if (count >= 2 && prefix[0] == 0xff && prefix[1] == 0xfe)
        {
            encoding = new UnicodeEncoding(false, false, true);
            skip = 2;
        }
        else if (count >= 2 && prefix[0] == 0xfe && prefix[1] == 0xff)
        {
            encoding = new UnicodeEncoding(true, false, true);
            skip = 2;
        }
        else if (count == 3 && prefix.AsSpan().SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            skip = 3;
        }

        stream.Position = skip;
        try
        {
            List<string> lines = await ReadDecodedLinesAsync(stream, encoding, token)
                .ConfigureAwait(false);
            Encoding preferred = skip switch
            {
                3 => new UTF8Encoding(true, true),
                2 => new UnicodeEncoding(prefix[0] == 0xfe, true, true),
                _ => encoding
            };
            return (lines, preferred);
        }
        catch (DecoderFallbackException) when (skip == 0)
        {
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            List<string> lines = await ReadDecodedLinesAsync(stream, ScriptEncodings.Ansi, token)
                .ConfigureAwait(false);
            return (lines, ScriptEncodings.Ansi);
        }
    }

    private static async Task<List<string>> ReadDecodedLinesAsync(
        Stream stream, Encoding encoding, CancellationToken token)
    {
        List<string> lines = [];
        using var reader = new StreamReader(stream, encoding,
            detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is string line)
        {
            token.ThrowIfCancellationRequested();
            lines.Add(line);
        }

        token.ThrowIfCancellationRequested();
        return lines;
    }

    internal static List<string> SplitLines(string text,
        CancellationToken token = default)
    {
        List<string> lines = [];
        using var reader = new StringReader(text);
        while (reader.ReadLine() is string line)
        {
            if ((lines.Count & 255) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            lines.Add(line);
        }

        token.ThrowIfCancellationRequested();
        return lines;
    }

    internal static string JoinLines(List<string> lines, bool finalCr) =>
        string.Join("\r\n", lines)
        + (finalCr && lines.Count > 0 ? "\r\n" : "");

    internal static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            return new UnicodeEncoding(false, true, true)
                .GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            return new UnicodeEncoding(true, true, true)
                .GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            return new UTF8Encoding(false, true)
                .GetString(bytes, 3, bytes.Length - 3);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return ScriptEncodings.Ansi.GetString(bytes);
        }
    }

    internal static Encoding WriteEncoding(int mode, string text,
        Encoding? preferred = null)
    {
        switch (mode & (int)ScriptFileMode.WriteMask)
        {
            case (int)ScriptFileMode.WriteAnsi:
                return ScriptEncodings.Ansi;
            case (int)ScriptFileMode.WriteUtf8:
                return new UTF8Encoding(false, true);
            case (int)ScriptFileMode.WriteUtf8Bom:
                return new UTF8Encoding(true, true);
            case (int)ScriptFileMode.WriteUtf16:
                return new UnicodeEncoding(false, true, true);
            default:
                try
                {
                    Encoding automatic = preferred ?? ScriptEncodings.Ansi;
                    _ = automatic.GetByteCount(text);
                    return automatic;
                }
                catch (EncoderFallbackException)
                {
                    return new UTF8Encoding(false, true);
                }
        }
    }

    internal static string FormatField(string value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && value[start] <= ' '
            && value[start] != '\t')
        {
            start++;
        }

        while (end > start && value[end - 1] <= ' '
            && value[end - 1] != '\t')
        {
            end--;
        }

        return value[start..end];
    }
}
