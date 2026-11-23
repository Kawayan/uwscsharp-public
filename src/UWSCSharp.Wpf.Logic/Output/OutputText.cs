using System.Text;

namespace UWSCSharp.Wpf;

/// <summary>
/// 出力欄に表示する文字列の量を制限します。
/// </summary>
internal static class OutputText
{
    /// <summary>
    /// 出力欄に保持する最大文字数です。
    /// </summary>
    public const int MaximumLength = 100_000;

    /// <summary>
    /// 削除後に残す目安の文字数です。削除を毎行繰り返さないよう余裕を持たせます。
    /// </summary>
    public const int RetainedLength = 80_000;

    /// <summary>
    /// 各行を上限で切り詰め、改行付きで連結します。
    /// </summary>
    public static string Join(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var builder = new StringBuilder();
        foreach (string line in lines)
        {
            int length = PrefixLength(line, MaximumLength);
            builder.Append(line, 0, length);
            builder.Append(Environment.NewLine);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 上限を超えた場合に先頭から削除する文字数を返します。
    /// 行の途中で切らないよう、残す範囲の直前の改行までを削除します。
    /// 改行で切ると全文が消える長い1行は、行の途中で切ります。
    /// </summary>
    public static int TrimCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= MaximumLength)
        {
            return 0;
        }

        int start = text.Length - RetainedLength;
        int newline = text.IndexOf('\n', start - 1);
        int count = newline < 0 || newline + 1 >= text.Length
            ? start : newline + 1;
        return SplitsSurrogatePair(text, count) ? count + 1 : count;
    }

    /// <summary>
    /// サロゲートペアを分断しない先頭部分の長さを返します。
    /// </summary>
    internal static int PrefixLength(string text, int maximum)
    {
        int length = Math.Min(text.Length, maximum);
        return SplitsSurrogatePair(text, length) ? length - 1 : length;
    }

    private static bool SplitsSurrogatePair(string text, int index) =>
        index > 0 && index < text.Length
        && char.IsHighSurrogate(text[index - 1])
        && char.IsLowSurrogate(text[index]);
}
