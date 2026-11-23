using System.Buffers.Binary;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 容量を確定した文字列領域を、元バイトとUTF-16コード単位を保って変換します。
/// </summary>
internal static class NativeDllTextCodec
{
    internal static byte[] EncodeArgument(NativeDllPrimitive type, Value value)
    {
        if (IsWide(type))
        {
            string text = BuiltinText.Convert(value);
            return EncodeUnicode(text, text.Length);
        }

        byte[] bytes = EncodedText.GetWindowsAnsiBytes(value);
        return EncodeAnsi(bytes, bytes.Length);
    }

    internal static Value DecodeArgument(NativeDllPrimitive type, byte[]? buffer)
    {
        bool nulTerminated = IsNulTerminated(type);
        if (IsWide(type))
        {
            return Value.String(buffer is null ? "" : ReadUnicode(buffer,
                buffer.Length / sizeof(char) - 1, nulTerminated));
        }

        byte[] bytes = buffer is null ? []
            : ReadAnsi(buffer, buffer.Length - 1, nulTerminated);
        return EncodedText.FromWindowsAnsi(bytes).ToValue();
    }

    internal static Value DecodeContents(NativeDllPrimitive type,
        ReadOnlySpan<byte> contents) => IsWide(type)
            ? Value.String(DecodeUnicode(contents))
            : EncodedText.FromWindowsAnsi(contents).ToValue();

    internal static bool IsWide(NativeDllPrimitive type) =>
        type is NativeDllPrimitive.Wstring or NativeDllPrimitive.Pwchar;

    internal static bool IsNulTerminated(NativeDllPrimitive type) =>
        type is NativeDllPrimitive.String or NativeDllPrimitive.Wstring;

    /// <summary>
    /// 容量はバイト数です。末尾のNUL用に追加の1バイトを確保します。
    /// </summary>
    internal static byte[] EncodeAnsi(ReadOnlySpan<byte> value, int capacity)
    {
        int length = BufferLength(capacity, 1);
        if (value.Length > capacity)
        {
            throw new ArgumentException("文字列のバイト列が容量を超えています。", nameof(value));
        }

        byte[] bytes = new byte[length];
        value.CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// 容量はUTF-16コード単位数です。終端の1コード単位を追加します。
    /// </summary>
    internal static byte[] EncodeUnicode(string value, int capacity)
    {
        ArgumentNullException.ThrowIfNull(value);
        int length = BufferLength(capacity, 2);
        if (value.Length > capacity)
        {
            throw new ArgumentException("文字列のコード単位数が容量を超えています。", nameof(value));
        }

        byte[] bytes = new byte[length];
        for (int index = 0; index < value.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2, 2), value[index]);
        }

        return bytes;
    }

    /// <summary>
    /// コードページ変換前のバイト列を返し、不完全な多バイト文字も保持します。
    /// </summary>
    internal static byte[] ReadAnsi(ReadOnlySpan<byte> buffer, int capacity, bool nulTerminated)
    {
        int length = ContentLength(buffer, capacity, 1, nulTerminated);
        return buffer[..length].ToArray();
    }

    /// <summary>
    /// UTF-16のコード単位を復元し、孤立サロゲートを置換しません。
    /// </summary>
    internal static string ReadUnicode(ReadOnlySpan<byte> buffer, int capacity, bool nulTerminated)
    {
        int length = ContentLength(buffer, capacity, 2, nulTerminated);
        return DecodeUnicode(buffer[..length]);
    }

    private static string DecodeUnicode(ReadOnlySpan<byte> bytes)
    {
        char[] characters = new char[bytes.Length / 2];
        for (int index = 0; index < characters.Length; index++)
        {
            characters[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(index * 2, 2));
        }

        return new string(characters);
    }

    private static int ContentLength(ReadOnlySpan<byte> buffer,
        int capacity, int unitSize, bool nulTerminated)
    {
        int expected = BufferLength(capacity, unitSize);
        if (buffer.Length != expected)
        {
            throw new ArgumentException("文字列領域の長さが容量と一致しません。", nameof(buffer));
        }

        if (!nulTerminated)
        {
            return expected - unitSize;
        }

        for (int offset = 0; offset < buffer.Length; offset += unitSize)
        {
            if (buffer[offset] == 0 && (unitSize == 1 || buffer[offset + 1] == 0))
            {
                return offset;
            }
        }

        throw new InvalidOperationException("所有する文字列領域内にNUL終端がありません。");
    }

    private static int BufferLength(int capacity, int unitSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        return checked((capacity + 1) * unitSize);
    }
}
