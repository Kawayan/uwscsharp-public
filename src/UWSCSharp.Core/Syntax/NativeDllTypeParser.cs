namespace UWSCSharp.Core;

/// <summary>
/// DLL宣言の型部分を読み、実行可否とABIの判定は後続へ渡します。
/// </summary>
internal static class NativeDllTypeParser
{
    internal static NativeDllType Parse(string text, int line,
        CancellationToken token = default)
    {
        var reader = new Reader(text, line, token);
        NativeDllType type = reader.ReadType(0);
        reader.RequireEnd();
        return type;
    }

    internal static IReadOnlyList<NativeDllType> ParseList(string text, int line,
        CancellationToken token = default)
    {
        var reader = new Reader(text, line, token);
        List<NativeDllType> types = [];
        if (!reader.AtEnd)
        {
            types.Add(reader.ReadType(0));
            while (reader.Take(','))
            {
                types.Add(reader.ReadType(0));
            }
        }

        reader.RequireEnd();
        return types.AsReadOnly();
    }

    private sealed class Reader
    {
        private readonly string text;
        private readonly int line;
        private readonly CancellationToken token;
        private int position;

        internal Reader(string text, int line, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(text);
            this.text = text;
            this.line = line;
            this.token = token;
            token.ThrowIfCancellationRequested();
        }

        internal bool AtEnd
        {
            get
            {
                SkipWhiteSpace();
                return position == text.Length;
            }
        }

        internal NativeDllType ReadType(int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth >= ScriptLimits.MaxExpressionNesting)
            {
                throw Error("型の入れ子が深すぎます。");
            }

            SkipWhiteSpace();
            bool byReference = false;
            string name = ReadWord();
            if (name.Equals(Keywords.Var, StringComparison.OrdinalIgnoreCase))
            {
                byReference = true;
                SkipWhiteSpace();
                name = ReadWord();
            }

            if (name.Length == 0 && Take('{'))
            {
                List<NativeDllType> fields = [ReadType(depth + 1)];
                while (Take(','))
                {
                    fields.Add(ReadType(depth + 1));
                }

                Require('}');
                return new NativeDllStructureType(fields, byReference, ReadArraySuffix());
            }

            if (name.Length == 0 || !char.IsAsciiLetter(name[0])
                || !Enum.TryParse(name, ignoreCase: true, out NativeDllPrimitive primitive)
                || !Enum.IsDefined(primitive))
            {
                throw Error($"型名を認識できません: {name}");
            }

            return new NativeDllScalarType(primitive, byReference, ReadArraySuffix());
        }

        private bool ReadArraySuffix()
        {
            if (!Take('['))
            {
                return false;
            }

            Require(']');
            return true;
        }

        private string ReadWord()
        {
            SkipWhiteSpace();
            int start = position;
            while (position < text.Length
                && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_'))
            {
                token.ThrowIfCancellationRequested();
                position++;
            }

            return text[start..position];
        }

        internal bool Take(char value)
        {
            SkipWhiteSpace();
            if (position < text.Length && text[position] == value)
            {
                position++;
                return true;
            }

            return false;
        }

        private void Require(char value)
        {
            if (!Take(value))
            {
                throw Error($"'{value}'が必要です。");
            }
        }

        internal void RequireEnd()
        {
            if (!AtEnd)
            {
                throw Error("型の後に余分な文字があります。");
            }
        }

        private void SkipWhiteSpace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                token.ThrowIfCancellationRequested();
                position++;
            }

            token.ThrowIfCancellationRequested();
        }

        private ScriptException Error(string message) =>
            new(line, $"{Keywords.DefDll}: 型指定の{position + 1}文字目: {message}");
    }
}
