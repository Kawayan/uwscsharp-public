namespace UWSCSharp.Core;

/// <summary>
/// 認識語の内部配列経路で行う特殊文字表記の変換です。
/// </summary>
internal static class SpeechRecognitionSpecialCharacters
{
    internal static string Expand(string text, bool preserveSpecialCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        // 原版はアンダースコア付き表記を先に処理し、大小文字を区別します。
        string result = text.Replace("<#_DBL>", "\"", StringComparison.Ordinal)
            .Replace("<#_CR>", "\r\n", StringComparison.Ordinal)
            .Replace("<#_TAB>", "\t", StringComparison.Ordinal);
        if (!preserveSpecialCharacters)
        {
            result = result.Replace("<#DBL>", "\"", StringComparison.Ordinal)
                .Replace("<#CR>", "\r\n", StringComparison.Ordinal)
                .Replace("<#TAB>", "\t", StringComparison.Ordinal);
        }

        return result;
    }
}
