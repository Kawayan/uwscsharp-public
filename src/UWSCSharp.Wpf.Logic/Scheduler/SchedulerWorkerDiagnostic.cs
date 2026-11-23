using System.Text;

namespace UWSCSharp.Wpf;

/// <summary>
/// 通信へ載せる診断だけを短縮し、不正な文字を明示的に置換します。
/// </summary>
internal static class SchedulerWorkerDiagnostic
{
    internal static string? Limit(string? value)
    {
        if (value is null)
        {
            return null;
        }
        const int maximum = 4096;
        var text = new StringBuilder();
        int consumed = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            string piece = rune.Value == 0 ? "\\0" : rune.ToString();
            if (text.Length + piece.Length > maximum)
            {
                break;
            }
            text.Append(piece);
            consumed += rune.Utf16SequenceLength;
        }
        if (consumed < value.Length)
        {
            text.Append($"…（診断を{value.Length - consumed}コード単位省略）");
        }
        return text.ToString();
    }
}
