using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;

namespace UWSCSharp.Core;

/// <summary>
/// CP932のバイト操作と、明示した符号化変換を提供します。
/// </summary>
internal static class ByteTextOperations
{
    internal static int Length(Value value) =>
        value.Kind is ValueKind.Array or ValueKind.HashTable
            ? LengthOperations.Length(value) : EncodedText.Read(value).CopyBytes().Length;

    internal static Value Copy(Value value, Value start, Value count)
    {
        var text = EncodedText.Read(value);
        byte[] bytes = text.CopyBytes();
        int first = (int)Math.Clamp(Math.Truncate(
            NumericOperations.RuntimeNumber(start)) - 1, 0, bytes.Length);
        int length = count.Kind is ValueKind.Empty or ValueKind.Omitted
            ? bytes.Length - first : (int)Math.Clamp(Math.Truncate(
                NumericOperations.RuntimeNumber(count)), 0, bytes.Length - first);
        return new EncodedText(bytes.AsSpan(first, length), text.CodePage).ToValue();
    }

    internal static int Asc(Value value)
    {
        byte[] bytes = EncodedText.Read(value).CopyBytes();
        return bytes.Length == 0 ? 0 : bytes[0];
    }

    internal static Value Chr(Value value) => new EncodedText(
        [unchecked((byte)NumericOperations.Integer(value))], 932).ToValue();

    private static bool Lead(byte value) => value is >= 0x81 and <= 0x9f
        or >= 0xe0 and <= 0xfc;

    private static int Width(byte[] bytes, int offset) =>
        Lead(bytes[offset]) && offset + 1 < bytes.Length
        && bytes[offset + 1] is >= 0x40 and <= 0xfc and not 0x7f ? 2 : 1;

    private static byte[] Fold(byte[] bytes)
    {
        Encoding encoding = EncodedText.EncodingFor(932);
        for (int i = 0; i < bytes.Length;)
        {
            int width = Width(bytes, i);
            if (width == 1)
            {
                if (bytes[i] is >= (byte)'a' and <= (byte)'z')
                {
                    bytes[i] -= 32;
                }
            }
            else
            {
                string text = encoding.GetString(bytes, i, 2);
                string upper = text.ToUpperInvariant();
                if (upper != text)
                {
                    byte[] folded = encoding.GetBytes(upper);
                    if (folded.Length == 2)
                    {
                        bytes[i] = folded[0];
                        bytes[i + 1] = folded[1];
                    }
                }
            }

            i += width;
        }

        return bytes;
    }

    internal static int Position(Value needleValue, Value sourceValue,
        Value occurrenceValue, bool sameString)
    {
        byte[] needle = EncodedText.Read(needleValue).CopyBytes();
        byte[] source = EncodedText.Read(sourceValue).CopyBytes();
        // ANSIの検索はNULで終わり、一致の両端を文字境界にそろえます。
        int needleEnd = Array.IndexOf(needle, (byte)0);
        int sourceEnd = Array.IndexOf(source, (byte)0);
        if (needleEnd >= 0)
        {
            needle = needle[..needleEnd];
        }

        if (sourceEnd >= 0)
        {
            source = source[..sourceEnd];
        }

        double occurrence = occurrenceValue.Kind is ValueKind.Empty or ValueKind.Omitted
            ? 1 : Math.Truncate(NumericOperations.RuntimeNumber(occurrenceValue));
        if (occurrence == 0)
        {
            occurrence = 1;
        }

        if (needle.Length == 0 || Math.Abs(occurrence) > source.Length)
        {
            return 0;
        }

        if (!sameString)
        {
            needle = Fold(needle);
            source = Fold(source);
        }

        bool[] boundaries = new bool[source.Length + 1];
        for (int i = 0; i < source.Length; i += Width(source, i))
        {
            boundaries[i] = true;
        }

        boundaries[source.Length] = true;
        List<int> matches = [];
        for (int i = 0; i < source.Length; i += Width(source, i))
        {
            if (needle.Length <= source.Length - i
                && boundaries[i + needle.Length] && source.AsSpan(i).StartsWith(needle))
            {
                matches.Add(i + 1);
                if (occurrence > 0 && matches.Count == occurrence)
                {
                    return i + 1;
                }
            }
        }

        int index = matches.Count + (int)occurrence;
        return occurrence < 0 && index >= 0 ? matches[index] : 0;
    }

    internal static string ConvertText(Value value, Value modeValue)
    {
        int mode = NumericOperations.Integer(modeValue);
        const int lower = 256;
        const int upper = 512;
        const int hiragana = 1048576;
        const int katakana = 2097152;
        const int half = 4194304;
        const int full = 8388608;
        if ((mode & ~(lower | upper | hiragana | katakana | half | full)) != 0
            || (mode & (lower | upper)) == (lower | upper)
            || (mode & (hiragana | katakana)) == (hiragana | katakana)
            || (mode & (half | full)) == (half | full))
        {
            throw new InvalidOperationException("STRCONVの変換指定が不正です。");
        }

        string text = BuiltinText.Convert(value);
        if (mode == 0)
        {
            return text;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("STRCONVにはWindowsの日本語変換が必要です。");
        }

        VbStrConv flags = (mode & lower) != 0 ? VbStrConv.Lowercase : 0;
        flags |= (mode & upper) != 0 ? VbStrConv.Uppercase : 0;
        flags |= (mode & hiragana) != 0 ? VbStrConv.Hiragana : 0;
        flags |= (mode & katakana) != 0 ? VbStrConv.Katakana : 0;
        flags |= (mode & half) != 0 ? VbStrConv.Narrow : 0;
        flags |= (mode & full) != 0 ? VbStrConv.Wide : 0;
        try
        {
            return Strings.StrConv(text, flags, 1041) ?? "";
        }
        catch (ArgumentException failure)
        {
            throw new InvalidOperationException("STRCONVの変換に失敗しました。", failure);
        }
    }

    internal static Value Encode(Value value, Value modeValue)
    {
        int mode = NumericOperations.Integer(modeValue);
        string text = BuiltinText.Convert(value);
        switch (mode)
        {
            case 1:
                return new EncodedText(EncodedText.EncodingFor(932).GetBytes(text), 932).ToValue();
            case 2:
                var url = new StringBuilder();
                foreach (byte b in EncodedText.Read(value).CopyBytes())
                {
                    if (b is >= (byte)'A' and <= (byte)'Z'
                        or >= (byte)'a' and <= (byte)'z'
                        or >= (byte)'0' and <= (byte)'9'
                        or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'!'
                        or (byte)'*' or (byte)'\'' or (byte)'(' or (byte)')')
                    {
                        url.Append((char)b);
                    }
                    else
                    {
                        url.Append(b == 32 ? "+" : "%" + b.ToString("X2", CultureInfo.InvariantCulture));
                    }
                }

                return new EncodedText(Encoding.ASCII.GetBytes(url.ToString()), 932).ToValue();
            case 3:
                return new EncodedText(Utf8Bytes(text), 65001).ToValue();
            case 4:
                return Value.String(text.Replace("&", "&amp;").Replace("<", "&lt;")
                    .Replace(">", "&gt;").Replace("\"", "&quot;"));
            case 5:
                return ByteArray(EncodedText.EncodingFor(932).GetBytes(text));
            case 6:
                if ((long)text.Length * 2 > UwscArray.MaximumElements)
                {
                    throw new InvalidOperationException("バイト配列の要素数が上限を超えました。");
                }

                byte[] wide = new byte[text.Length * 2];
                for (int i = 0; i < text.Length; i++)
                {
                    wide[i * 2] = (byte)text[i];
                    wide[i * 2 + 1] = (byte)(text[i] >> 8);
                }

                return ByteArray(wide);
            default:
                throw new InvalidOperationException("ENCODEの変換指定が不正です。");
        }
    }

    private static Value ByteArray(byte[] bytes)
    {
        if (bytes.Length > UwscArray.MaximumElements)
        {
            throw new InvalidOperationException("バイト配列の要素数が上限を超えました。");
        }

        return Value.FromArray(UwscArray.FromValues(bytes.Select(b => Value.Numeric(b)))
            .WithNativeElementType(17));
    }

    internal static Value Decode(Value value, Value modeValue)
    {
        int mode = NumericOperations.Integer(modeValue);
        if (mode is 5 or 6)
        {
            UwscArray array = value.AsArray();
            if (array.Rank != 1 || array.NativeElementType != 17)
            {
                throw new InvalidOperationException("型付きの1次元バイト配列を指定してください。");
            }

            byte[] bytes = new byte[array.Count];
            for (int i = 0; i < bytes.Length; i++)
            {
                Value element = array.Values[i];
                if (element.Kind != ValueKind.Number || !double.IsFinite(element.Number)
                    || element.Number is < 0 or > 255 || element.Number != Math.Truncate(element.Number))
                {
                    throw new InvalidOperationException("バイト配列の値が不正です。");
                }

                bytes[i] = (byte)element.Number;
            }

            if (mode == 5)
            {
                return new EncodedText(bytes, 932).ToValue();
            }

            if (bytes.Length % 2 != 0)
            {
                throw new InvalidOperationException("UTF16のバイト数は偶数で指定してください。");
            }

            char[] wide = new char[bytes.Length / 2];
            for (int i = 0; i < wide.Length; i++)
            {
                wide[i] = (char)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
            }

            return Value.String(new string(wide));
        }

        string text = BuiltinText.Convert(value);
        switch (mode)
        {
            case 2:
                List<byte> result = [];
                for (int i = 0; i < text.Length;)
                {
                    if (text[i] == '%')
                    {
                        if (i + 2 >= text.Length || !byte.TryParse(text.AsSpan(i + 1, 2),
                            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b))
                        {
                            throw new InvalidOperationException("URLの%表記が不正です。");
                        }

                        result.Add(b);
                        i += 3;
                    }
                    else
                    {
                        int length = char.IsSurrogatePair(text, i) ? 2 : 1;
                        result.AddRange(EncodedText.EncodingFor(932).GetBytes(
                            text[i] == '+' ? " " : text.Substring(i, length)));
                        i += length;
                    }
                }

                return new EncodedText(result.ToArray(), 932).ToValue();
            case 3:
                return Value.String(Utf8Text(EncodedText.Read(value).CopyBytes()));
            case 4:
                return Value.String(Regex.Replace(text, "&(?:amp|lt|gt|quot|#[0-9]+|#[xX][0-9A-Fa-f]+);",
                    match => HtmlEntity(match.Value)));
            default:
                throw new InvalidOperationException("DECODEの変換指定が不正です。");
        }
    }

    private static byte[] Utf8Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Utf8Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string HtmlEntity(string entity)
    {
        string? named = entity switch
        {
            "&amp;" => "&",
            "&lt;" => "<",
            "&gt;" => ">",
            "&quot;" => "\"",
            _ => null
        };
        if (named is not null)
        {
            return named;
        }

        bool hex = entity[2] is 'x' or 'X';
        ReadOnlySpan<char> digits = entity.AsSpan(hex ? 3 : 2, entity.Length - (hex ? 4 : 3));
        return int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out int scalar) && scalar is >= 0 and <= 0x10ffff
            && scalar is not (>= 0xd800 and <= 0xdfff)
                ? char.ConvertFromUtf32(scalar) : entity;
    }
}
