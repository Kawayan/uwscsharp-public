using System.Text;

namespace UWSCSharp.Wpf;

/// <summary>
/// UIスレッドで扱う、出力件数と文字数に上限がある表示履歴です。
/// </summary>
internal sealed class OutputHistory
{
    private readonly Queue<string> entries = new();
    private int characterCount;
    private int maximumEntries = 2000;

    internal int Count => entries.Count;

    /// <summary>
    /// 新しい実行用に履歴と既定の保持件数を戻します。
    /// </summary>
    internal void Reset()
    {
        entries.Clear();
        characterCount = 0;
        maximumEntries = 2000;
    }

    /// <summary>
    /// 表示用の保持件数を変更し、上限を超えた古い出力を除きます。
    /// </summary>
    internal void SetMaximumEntries(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, 100_000);
        maximumEntries = maximum;
        while (entries.Count > maximumEntries)
        {
            RemoveOldest();
        }
    }

    /// <summary>
    /// 本文を表示用に制限し、古い出力から除いて追加します。
    /// </summary>
    internal void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumEntries == 0)
        {
            return;
        }

        int length = OutputText.PrefixLength(text,
            OutputText.MaximumLength - Environment.NewLine.Length);
        string entry = text[..length];
        int size = entry.Length + Environment.NewLine.Length;
        while (entries.Count > 0 && (entries.Count >= maximumEntries
            || characterCount + size > OutputText.MaximumLength))
        {
            RemoveOldest();
        }

        entries.Enqueue(entry);
        characterCount += size;
    }

    /// <summary>
    /// 改行付きの表示本文を返します。保持中の本文は変更しません。
    /// </summary>
    internal string Render()
    {
        var text = new StringBuilder(characterCount);
        foreach (string entry in entries)
        {
            text.Append(entry);
            text.Append(Environment.NewLine);
        }

        return text.ToString();
    }

    private void RemoveOldest()
    {
        characterCount -= entries.Dequeue().Length + Environment.NewLine.Length;
    }
}
