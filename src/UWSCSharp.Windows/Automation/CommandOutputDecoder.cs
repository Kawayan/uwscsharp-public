using System.Runtime.InteropServices;
using System.Text;

namespace UWSCSharp.Windows;

/// <summary>
/// 出力の全バイトを厳密に変換し、NULと改行を保持します。
/// </summary>
internal static partial class CommandOutputDecoder
{
    internal static string DecodeUtf8(byte[] bytes)
    {
        ReadOnlySpan<byte> text = bytes;
        if (text.StartsWith("\uFEFF"u8))
        {
            text = text[3..];
        }

        return new UTF8Encoding(false, true).GetString(text);
    }

    internal static string Decode(byte[] bytes, bool unicode, int? codePage)
    {
        Encoding encoding;
        if (unicode)
        {
            encoding = new UnicodeEncoding(false, false, true);
        }
        else
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(codePage ?? checked((int)GetACP()),
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        return encoding.GetString(bytes);
    }
}
