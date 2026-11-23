using System.Text;

namespace UWSCSharp.Core;

/// <summary>
/// スクリプトで使う厳密な文字コード変換を共有します。
/// </summary>
internal static class ScriptEncodings
{
    private static readonly Lazy<Encoding> CodePage932 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    });

    internal static Encoding Ansi => CodePage932.Value;
}
