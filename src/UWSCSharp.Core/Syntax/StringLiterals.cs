namespace UWSCSharp.Core;

/// <summary>
/// 文字列リテラルの終端を各構文処理で共有します。
/// </summary>
internal static class StringLiterals
{
    internal static bool TryReadEnd(string text, int start,
        out int end, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        end = start + 1;
        while (end < text.Length && text[end] != '"')
        {
            if ((end & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            end++;
        }

        if (end == text.Length)
        {
            return false;
        }

        // 終端の連続引用符は、最後の1個だけを閉じ引用符にします。
        while (end < text.Length && text[end] == '"')
        {
            if ((end & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            end++;
        }

        return true;
    }
}
