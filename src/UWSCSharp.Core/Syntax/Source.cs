using System.Text;

namespace UWSCSharp.Core;

/// <summary>
/// スクリプトの読み込みと文の分割を提供します。
/// </summary>
public static class Source
{
    /// <summary>
    /// UTF-8、コードページ932、またはUWS05でソースファイルを読み込みます。
    /// </summary>
    public static string ReadFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (ScrambledScript.HasSignature(bytes))
        {
            return ScrambledScript.Decode(bytes);
        }

        return DecodeText(bytes);
    }

    internal static string DecodeText(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(
            new byte[] { 0xef, 0xbb, 0xbf }))
        {
            return new UTF8Encoding(false, true).GetString(
                bytes, 3, bytes.Length - 3);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(
                bytes);
        }
        catch (DecoderFallbackException)
        {
            return ScriptEncodings.Ansi.GetString(bytes);
        }
    }

    /// <summary>
    /// 行結合と複文を処理し、開始物理行付きの文を返します。
    /// </summary>
    public static IEnumerable<(int Line, string Text)> Statements(
        string source)
        => Statements(source, CancellationToken.None);

    /// <summary>
    /// 取消を確認しながら、開始物理行付きの文を返します。
    /// </summary>
    public static IEnumerable<(int Line, string Text)> Statements(
        string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new StringReader(source);
        var pending = new StringBuilder();
        int line = 0;
        int start = 1;
        bool continued = false;
        while (reader.ReadLine() is { } physical)
        {
            cancellationToken.ThrowIfCancellationRequested();
            line++;
            if (!continued)
            {
                start = line;
            }

            if (!continued && IsTextBlockHeader(physical))
            {
                int comment = physical.IndexOf("//", StringComparison.Ordinal);
                string header = (comment < 0 ? physical : physical[..comment]).Trim();
                List<string> raw = [];
                bool closed = false;
                while (reader.ReadLine() is { } body)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    line++;
                    int endComment = body.IndexOf("//", StringComparison.Ordinal);
                    string terminator = (endComment < 0 ? body : body[..endComment]).Trim();
                    if (terminator.Equals(Keywords.EndTextBlock, StringComparison.OrdinalIgnoreCase))
                    {
                        closed = true;
                        break;
                    }

                    raw.Add(body);
                }

                if (!closed)
                {
                    throw new ScriptException(start, "ENDTEXTBLOCKがありません。");
                }

                // 本文の字下げ・コメント記号・引用符を通常の文分割へ渡しません。
                yield return (start, header + "\n" + string.Join("\r\n", raw));
                continue;
            }

            int end = physical.Length;
            // 行結合で生成した // も、この物理行の末尾までを除外します。
            // 保留行の末尾が / なら、閉じた文字列の外にあります。
            bool joinedComment = continued && pending.Length > 0
                && pending[^1] == '/'
                && physical.AsSpan().TrimStart().StartsWith("/");
            if (joinedComment)
            {
                pending.Length--;
                end = 0;
            }

            for (int i = 0; !joinedComment && i < physical.Length; i++)
            {
                if ((i & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (physical[i] == '"')
                {
                    if (!StringLiterals.TryReadEnd(physical, i,
                        out int afterLiteral, cancellationToken))
                    {
                        throw new ScriptException(line, "文字列が閉じていません。");
                    }

                    i = afterLiteral - 1;
                    continue;
                }

                if (physical[i] == '/' && i + 1
                    < physical.Length && physical[i + 1] == '/')
                {
                    end = i;
                    break;
                }
            }

            string text = physical[..end].TrimEnd();
            // 結合途中のコメント行は、保留中の文を終端しません。
            if (continued && !joinedComment && end < physical.Length
                && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // 継続行の字下げは、結合後のトークンに含めません。
            if (continued)
            {
                text = text.TrimStart();
            }

            continued = text.EndsWith('_');
            pending.Append(continued ? text[..^1] : text);
            if (continued)
            {
                continue;
            }

            int offset = 0;
            string logical = pending.ToString();
            for (int i = 0; i <= logical.Length; i++)
            {
                if ((i & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (i < logical.Length && logical[i] == '"')
                {
                    if (!StringLiterals.TryReadEnd(logical, i,
                        out int afterLiteral, cancellationToken))
                    {
                        throw new ScriptException(start, "文字列が閉じていません。");
                    }

                    i = afterLiteral - 1;
                    continue;
                }

                if (i == logical.Length || logical[i] == ';')
                {
                    // 空文や前後の空白には文字列を割り当てません。
                    ReadOnlySpan<char> statement = logical.AsSpan(
                        offset, i - offset).Trim();
                    if (!statement.IsEmpty)
                    {
                        string statementText =
                            statement.Length == logical.Length
                            ? logical : statement.ToString();
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return (start, statementText);
                    }

                    offset = i + 1;
                }
            }

            pending.Clear();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (continued)
        {
            throw new ScriptException(start, "行結合の次の行がありません。");
        }
    }
    private static bool IsTextBlockHeader(string physical)
    {
        ReadOnlySpan<char> text = physical.AsSpan().TrimStart();
        return text.StartsWith(Keywords.TextBlock, StringComparison.OrdinalIgnoreCase)
            && (text.Length == Keywords.TextBlock.Length
                || char.IsWhiteSpace(text[Keywords.TextBlock.Length]));
    }

}
