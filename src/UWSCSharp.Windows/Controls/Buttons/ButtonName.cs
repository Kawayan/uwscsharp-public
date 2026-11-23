namespace UWSCSharp.Windows;

internal static class ButtonName
{
    internal static string Normalize(string text)
    {
        int tab = text.IndexOf('\t');
        if (tab >= 0)
        {
            text = text[..tab];
        }

        int start = 0;
        int end = text.Length;
        while (start < end && text[start] <= ' ')
        {
            start++;
        }

        while (end > start && text[end - 1] <= ' ')
        {
            end--;
        }

        text = text[start..end];
        if (text.Length < 3)
        {
            return text;
        }

        text = text.TrimEnd(':', '.', ' ');
        if (text.Length > 3 && text[^1] == ')')
        {
            if (text[^3] == '(')
            {
                text = text[..^3];
            }
            else if (text[^4] == '(' && text[^3] is '&' or '_')
            {
                text = text[..^4];
            }
        }

        // 原版の後段は連続したアンパサンドもすべて除去します。
        return text.Replace("&", "", StringComparison.Ordinal);
    }
}
