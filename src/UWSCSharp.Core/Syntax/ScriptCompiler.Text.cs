namespace UWSCSharp.Core;

public static partial class ScriptCompiler
{
    internal static string Word(string text) => IdentifierPatterns.Prefix().Match(text).Value.ToUpperInvariant();

    internal static string Rest(string text) => text[Word(
        text).Length..].Trim();
    // 引用符で囲まれた文字列と括弧内の式を除き、キーワードと区切り文字を検索します。
    private static int Find(string text, string target)
    {
        int nesting = 0;
        for (int i = 0; i <= text.Length - target.Length; i++)
        {
            if (text[i] == '"')
            {
                if (!StringLiterals.TryReadEnd(text, i, out int afterLiteral))
                {
                    return -1;
                }

                i = afterLiteral - 1;
                continue;
            }

            if (text[i] is '(' or '[')
            {
                nesting++;
                continue;
            }

            if (text[i] is ')' or ']')
            {
                nesting--;
                continue;
            }

            if (nesting != 0 || !text.AsSpan(i,
                target.Length).Equals(target.AsSpan(),
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (target.Length > 1 && (IsMemberName(text, i) || (i > 0
                && IsNameCharacter(text[i - 1])) || (i + target.Length
                < text.Length && IsNameCharacter(text[i + target.Length]))))
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static bool IsMemberName(string text, int index)
    {
        int dot = index - 1;
        while (dot >= 0 && char.IsWhiteSpace(text[dot]))
        {
            dot--;
        }

        if (dot < 0 || text[dot] != '.')
        {
            return false;
        }

        // 1. THENの末尾小数点をメンバー参照と区別します。
        int digit = dot - 1;
        while (digit >= 0 && text[digit] is >= '0' and <= '9')
        {
            digit--;
        }

        return digit == dot - 1 || (digit >= 0
            && (IsNameCharacter(text[digit]) || text[digit] is '.' or '$'));
    }

    private static bool IsNameCharacter(
        char c) => char.IsLetterOrDigit(c) || c == '_';

    private static IEnumerable<string> Split(string text,
        char separator)
    {
        while (true)
        {
            int index = Find(text, separator.ToString());
            if (index < 0)
            {
                yield return text;
                yield break;
            }

            yield return text[..index];
            text = text[(index + 1)..];
        }
    }
}
