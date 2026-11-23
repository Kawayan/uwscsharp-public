namespace UWSCSharp.Core;

/// <summary>
/// UWSC互換の文字列操作を提供します。
/// </summary>
public static class StringOperations
{
    /// <summary>
    /// 有効なサロゲートペアを1文字として長さを求めます。
    /// </summary>
    public static int Length(string text) => LengthPrefix(text, text.Length);

    private static int LengthPrefix(string text, int end)
    {
        int count = 0;
        for (int offset = 0; offset < end; offset = NextOffset(text, offset))
        {
            count++;
        }

        return count;
    }

    private static int NextOffset(string text, int offset)
    {
        // 有効なペアだけを1文字とし、孤立したコード単位も保存します。
        return offset + (char.IsHighSurrogate(text[offset])
            && offset + 1 < text.Length
            && char.IsLowSurrogate(text[offset + 1]) ? 2 : 1);
    }

    private static int Offset(string text, int logical)
    {
        if (logical <= 0)
        {
            return 0;
        }

        int offset = 0;
        for (int count = 0; count < logical && offset < text.Length; count++)
        {
            offset = NextOffset(text, offset);
        }

        return offset;
    }

    /// <summary>
    /// 指定位置から指定長の文字列を取り出します。
    /// </summary>
    public static string Copy(string text, int start,
        int? count = null)
    {
        if (count < 0)
        {
            return "";
        }

        int first = Math.Max(start, 1) - 1;
        int offset = Offset(text, first);
        int end = count is null ? text.Length : Offset(text,
            (int)Math.Min(int.MaxValue, (long)first + count.Value));
        return text[offset..Math.Max(offset, end)];
    }

    /// <summary>
    /// 指定した文字列の出現位置を検索します。
    /// </summary>
    public static int Position(string needle, string source,
        int occurrence = 1, bool sameString = false)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        if (occurrence == 0)
        {
            occurrence = 1;
        }

        if (Math.Abs((long)occurrence) > source.Length)
        {
            return 0;
        }

        if (occurrence > 0)
        {
            int start = 0;
            for (int matched = 0; matched < occurrence;)
            {
                int found = FindForwardMatch(source, needle, start,
                    sameString ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    return 0;
                }

                if (++matched == occurrence)
                {
                    return LengthPrefix(source, found) + 1;
                }

                start = found + 1;
            }
        }
        else
        {
            int end = source.Length;
            for (int matched = 0; matched < -occurrence;)
            {
                if (end == 0)
                {
                    return 0;
                }

                int found = source.LastIndexOf(needle, end
                    - 1, end, sameString ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    return 0;
                }

                if (IsCharacterBoundary(source, found)
                    && IsCharacterBoundary(source, found + needle.Length)
                    && ++matched == -occurrence)
                {
                    return LengthPrefix(source, found) + 1;
                }

                end = found + needle.Length - 1;
            }
        }

        return 0;
    }

    private static bool IsCharacterBoundary(string text, int offset) =>
        offset == 0 || offset == text.Length
        || !char.IsLowSurrogate(text[offset])
        || !char.IsHighSurrogate(text[offset - 1]);

    internal static int FindForwardMatch(string text, string needle,
        int start, StringComparison comparison)
    {
        while (true)
        {
            int found = text.IndexOf(needle, start, comparison);
            if (found < 0 || (IsCharacterBoundary(text, found)
                && IsCharacterBoundary(text, found + needle.Length)))
            {
                return found;
            }

            start = found + 1;
        }
    }

    internal static int FindBackwardMatch(string text, string needle,
        int end, StringComparison comparison)
    {
        while (end > 0)
        {
            int found = text.AsSpan(0, end).LastIndexOf(needle.AsSpan(), comparison);
            if (found < 0 || (IsCharacterBoundary(text, found)
                && IsCharacterBoundary(text, found + needle.Length)))
            {
                return found;
            }

            end = found + needle.Length - 1;
        }

        return -1;
    }

    /// <summary>
    /// 指定した規則で文字列の端を取り除きます。
    /// </summary>
    public static string Trim(string text,
        bool fullWidth = false)
    {
        bool Remove(char c) => c <= '\x20' || (fullWidth
            && c == '\u3000');
        int start = 0;
        int end = text.Length;
        while (start < end && Remove(text[start]))
        {
            start++;
        }

        while (end > start && Remove(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    /// <summary>
    /// 文字列内の一致箇所を置換します。
    /// </summary>
    public static string Replace(string text, string from,
        string to, bool sameString = false)
    {
        if (from.Length == 0)
        {
            return text;
        }

        StringComparison comparison = sameString
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        long length = text.Length;
        int matches = 0;
        int start = 0;
        int found;
        while ((found = FindForwardMatch(text, from, start, comparison)) >= 0)
        {
            matches++;
            length += (long)to.Length - from.Length;
            start = found + from.Length;
        }

        if (matches == 0)
        {
            return text;
        }

        if (length > int.MaxValue)
        {
            throw new InvalidOperationException("置換結果が文字列の長さの範囲を超えます。");
        }

        // 先に長さを検査し、元の文字列にある一致だけを一度ずつ置換します。
        return string.Create((int)length, (text, from, to, comparison),
            static (destination, state) =>
            {
                int start = 0;
                int output = 0;
                int found;
                while ((found = FindForwardMatch(state.text, state.from,
                    start, state.comparison)) >= 0)
                {
                    state.text.AsSpan(start, found - start).CopyTo(destination[output..]);
                    output += found - start;
                    state.to.AsSpan().CopyTo(destination[output..]);
                    output += state.to.Length;
                    start = found + state.from.Length;
                }

                state.text.AsSpan(start).CopyTo(destination[output..]);
            });
    }

    /// <summary>
    /// 区切り文字の集合で先頭を取り出し、残りの文字列を返します。
    /// </summary>
    public static (string Token, Value Remainder) Token(
        string delimiters, string source, bool skipEmpty = false,
        bool quotes = false)
    {
        var separators = new HashSet<int>();
        for (int offset = 0; offset < delimiters.Length; offset = NextOffset(delimiters, offset))
        {
            separators.Add(CharacterValue(delimiters, offset));
        }

        bool IsDelimiter(int offset) => separators.Contains(CharacterValue(source, offset))
            && !(quotes && source[offset] == '"');
        int start = 0;
        if (skipEmpty)
        {
            while (start < source.Length
                && IsDelimiter(start))
            {
                start = NextOffset(source, start);
            }
        }

        bool quoted = false;
        for (int i = start; i < source.Length; i = NextOffset(source, i))
        {
            if (quotes && source[i] == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && IsDelimiter(i))
            {
                int next = NextOffset(source, i);
                if (skipEmpty)
                {
                    while (next < source.Length
                        && IsDelimiter(next))
                    {
                        next = NextOffset(source, next);
                    }
                }

                return (source[start..i], Value.String(
                    source[next..]));
            }
        }

        return (source[start..], Value.Empty);
    }

    private static int CharacterValue(string text, int offset) =>
        NextOffset(text, offset) - offset == 2
            ? char.ConvertToUtf32(text[offset], text[offset + 1]) : text[offset];
}
