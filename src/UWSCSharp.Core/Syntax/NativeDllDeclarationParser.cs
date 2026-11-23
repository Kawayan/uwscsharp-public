namespace UWSCSharp.Core;

/// <summary>
/// キーワードとコメントを除いたDLL宣言の本体を読み取ります。
/// </summary>
internal static class NativeDllDeclarationParser
{
    internal static NativeDllDeclaration Parse(string text, int line,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        token.ThrowIfCancellationRequested();
        int open = text.IndexOf('(');
        int close = text.IndexOf(')');
        if (open < 1 || close < open)
        {
            throw Error(line, "関数名と引数一覧が必要です。");
        }

        string name = text[..open].Trim();
        Names.Validate(name, line);
        IReadOnlyList<NativeDllType> parameters = NativeDllTypeParser.ParseList(
            text[(open + 1)..close], line, token);
        string tail = text[(close + 1)..].Trim();
        if (!tail.StartsWith(':'))
        {
            throw Error(line, "引数一覧の後に':'が必要です。");
        }

        tail = tail[1..].Trim();
        List<int> separators = FindSeparators(tail, line, token);
        bool packed = false;
        if (tail.EndsWith(Keywords.Pack, StringComparison.OrdinalIgnoreCase))
        {
            int boundary = tail.Length - Keywords.Pack.Length - 1;
            if (boundary >= 0 && separators.Contains(boundary))
            {
                packed = true;
                tail = tail[..boundary].TrimEnd();
            }
        }

        NativeDllType? returnType = null;
        // ドライブ文字のコロンと、戻り型を区切るコロンを区別します。
        int colon = FindSeparators(tail, line, token)
            .FirstOrDefault(index => tail[index] == ':', -1);
        bool drivePath = colon == 1 && char.IsAsciiLetter(tail[0])
            && tail.Length > 2 && (tail[2] == '\\' || tail[2] == '/');
        if (colon >= 0 && !drivePath)
        {
            returnType = NativeDllTypeParser.Parse(tail[..colon], line, token);
            tail = tail[(colon + 1)..].Trim();
        }

        if (tail.Length == 0)
        {
            throw Error(line, "DLL名が必要です。");
        }

        token.ThrowIfCancellationRequested();
        var declaration = new NativeDllDeclaration(name, parameters, returnType, tail, packed);
        NativeDllTypeRules.ValidateKnownConstraints(declaration, line, token);
        return declaration;
    }

    private static List<int> FindSeparators(string text, int line,
        CancellationToken token)
    {
        List<int> separators = [];
        char quote = '\0';
        for (int index = 0; index < text.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            char current = text[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    if (index + 1 < text.Length && text[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
            }
            else if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current == ':' || char.IsWhiteSpace(current))
            {
                separators.Add(index);
            }
        }

        if (quote != '\0')
        {
            throw Error(line, "DLL名の引用符が閉じていません。");
        }

        return separators;
    }

    private static ScriptException Error(int line, string message) =>
        new(line, $"{Keywords.DefDll}: {message}");
}
