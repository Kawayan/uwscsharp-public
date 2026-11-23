using System.Text;

namespace UWSCSharp.Core;

/// <summary>
/// 文字表示と、途中切出しを含む元のバイト列を分けて保持します。
/// </summary>
internal sealed class EncodedText
{
    private readonly byte[] bytes;

    internal EncodedText(ReadOnlySpan<byte> source, int codePage)
    {
        bytes = source.ToArray();
        CodePage = codePage;
        Text = EncodingFor(codePage).GetString(bytes);
    }

    internal int CodePage
    {
        get;
    }

    internal string Text
    {
        get;
    }

    internal byte[] CopyBytes() => (byte[])bytes.Clone();

    internal Value ToValue() => Value.String(Text) with { ByteText = this };

    internal static Encoding EncodingFor(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(codePage);
    }

    internal static EncodedText FromWindowsAnsi(ReadOnlySpan<byte> bytes) =>
        new(bytes, EncodingFor(0).CodePage);

    internal static byte[] GetWindowsAnsiBytes(Value value)
    {
        // コードページ0はWindowsのANSIコードページを選びます。
        Encoding encoding = EncodingFor(0);
        string text = BuiltinText.Convert(value);
        return value.ByteText is { } encoded
            && encoded.CodePage == encoding.CodePage && encoded.Text == text
                ? encoded.CopyBytes() : encoding.GetBytes(text);
    }

    internal static EncodedText Read(Value value) =>
        value.Kind == ValueKind.String && value.ByteText is { } encoded && value.Text == encoded.Text
            ? encoded : new(EncodingFor(932).GetBytes(
                BuiltinText.Convert(value)), 932);

    internal static Value? Concatenate(Value left, Value right)
    {
        if (left.Kind != ValueKind.String || right.Kind != ValueKind.String
            || left.ByteText is not { } a || right.ByteText is not { } b
            || a.CodePage != b.CodePage || left.Text != a.Text || right.Text != b.Text)
        {
            return null;
        }

        byte[] combined = new byte[checked(a.bytes.Length + b.bytes.Length)];
        a.bytes.CopyTo(combined, 0);
        b.bytes.CopyTo(combined, a.bytes.Length);
        return new EncodedText(combined, a.CodePage).ToValue();
    }
}
