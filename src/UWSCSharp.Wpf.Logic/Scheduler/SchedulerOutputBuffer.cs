using System.Text;

namespace UWSCSharp.Wpf;

/// <summary>
/// 予約ごとの表示を32,768コード単位に制限し、省略量を保持します。
/// </summary>
internal sealed class SchedulerOutputBuffer
{
    internal const int MaximumLength = 32_768;
    private readonly object sync = new();
    private readonly Queue<string> lines = [];
    private int maximumLines = 2000;
    private int length;
    private long omitted;

    internal long OmittedCodeUnits
    {
        get
        {
            lock (sync)
            {
                return omitted;
            }
        }
    }

    internal void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (sync)
        {
            int capacity = MaximumLength - Environment.NewLine.Length;
            int start = Math.Max(0, text.Length - capacity);
            if (start > 0 && char.IsLowSurrogate(text[start])
                && char.IsHighSurrogate(text[start - 1]))
            {
                start++;
            }

            omitted += start;
            string retained = text[start..] + Environment.NewLine;
            if (maximumLines == 0)
            {
                omitted += retained.Length;
                return;
            }

            lines.Enqueue(retained);
            length += retained.Length;
            Trim();
        }
    }

    internal void SetMaximumLines(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximum, 100_000);
        lock (sync)
        {
            maximumLines = maximum;
            Trim();
        }
    }

    internal string Render()
    {
        lock (sync)
        {
            var text = new StringBuilder(length);
            foreach (string line in lines)
            {
                text.Append(line);
            }

            return text.ToString();
        }
    }

    private void Trim()
    {
        while (lines.Count > maximumLines || length > MaximumLength)
        {
            int removed = lines.Dequeue().Length;
            length -= removed;
            omitted += removed;
        }
    }
}
