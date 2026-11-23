namespace UWSCSharp.Windows;

internal static class NativeListNameMatcher
{
    internal static bool Matches(string item, string names, bool partial)
    {
        string candidate = AsciiUpper(item);
        string desired = AsciiUpper(partial ? names : NativeListControl.Trim(names));
        return Compare(ref candidate, desired, partial);
    }

    internal static bool Compare(ref string candidate, string desired, bool partial)
    {
        if (candidate == desired || (partial
            ? candidate.Contains(desired, StringComparison.Ordinal)
            : NativeListControl.Trim(candidate) == desired))
        {
            return true;
        }

        // 変換後の候補は複数指定の次の比較にも引き継ぎます。
        candidate = HalfWidth(candidate);
        desired = HalfWidth(desired);
        return candidate == desired || (partial && candidate.Contains(desired, StringComparison.Ordinal));
    }

    internal static string AsciiUpper(string text)
    {
        return string.Create(text.Length, text, (result, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                char value = source[index];
                result[index] = value is >= 'a' and <= 'z' ? (char)(value - 32) : value;
            }
        });
    }

    internal static string HalfWidth(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        int length = Native.LCMapStringW(0x800, 0x400000, text, text.Length, null, 0);
        if (length == 0)
        {
            return text;
        }

        char[] result = new char[length];
        return Native.LCMapStringW(0x800, 0x400000, text, text.Length, result, length) == 0
            ? text : new string(result);
    }
}
