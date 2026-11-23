using System.Text.RegularExpressions;

namespace UWSCSharp.Core;

public static partial class ScriptCompiler
{
    private sealed partial class Parser
    {
        private IReadOnlyList<Statement> Block(int loops,
            bool topLevel, params string[] terminators)
        {
            if (++depth > ScriptLimits.MaxStatementNesting)
            {
                throw Error("構文の入れ子が深すぎます。");
            }

            try
            {
                var statements = new List<Statement>();
                while (position < source.Length
                    && !terminators.Contains(Keyword, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    (int Line, string Text) item = source[position++];
                    statements.Add(ParseStatement(item.Line,
                        item.Text, loops, topLevel) with
                    {
                        SourcePath = sourcePath
                    });
                }

                return statements.AsReadOnly();
            }
            finally
            {
                depth--;
            }
        }

        private Statement ParseStatement(int line,
            string text, int loops, bool topLevel)
        {
            string word = Word(text);
            string rest = Rest(text);
            switch (word)
            {
                case Keywords.With:
                {
                    Expr target = ParseExpression(rest, line);
                    IReadOnlyList<Statement> body = Block(loops, false, Keywords.EndWith);
                    End(Keywords.EndWith, line);
                    return new WithStatement(line, target, body);
                }

                case Keywords.TextBlock:
                    return ParseTextBlockStatement(line, text);

                case Keywords.If:
                case Keywords.IfB:
                    return ParseIfStatement(line, word, rest, loops);

                case Keywords.For:
                    return ParseForStatement(line, rest, loops);

                case Keywords.While:
                {
                    Expr condition = ParseExpression(rest, line, true);
                    IReadOnlyList<Statement> body = Block(
                        loops + 1, false, Keywords.Wend);
                    End(Keywords.Wend, line);
                    return new WhileStatement(line,
                        condition, body);
                }

                case Keywords.Repeat:
                {
                    RequireEmpty(rest, line);
                    IReadOnlyList<Statement> body = Block(
                        loops + 1, false, Keywords.Until);
                    if (Keyword != Keywords.Until)
                    {
                        throw Error("UNTILがありません。", line);
                    }

                    (int Line, string Text) until = source[position++];
                    return new RepeatStatement(line,
                        ParseExpression(Rest(until.Text), until.Line, true), until.Line,
                        body);
                }

                case Keywords.Select:
                    return ParseSelectStatement(line, rest, loops);

                case Keywords.Try:
                    return ParseTryStatement(line, rest, loops);

                case Keywords.Public:
                case Keywords.Const:
                    return ParseGlobalDeclarationStatement(
                        line, word, rest, topLevel);

                default:
                    return Simple(line, text, loops);
            }
        }

        private Statement ParseTextBlockStatement(int line, string text)
        {
            int separator = text.IndexOf('\n');
            if (separator < 0)
            {
                throw Error("TEXTBLOCKの本文がありません。", line);
            }

            string name = Rest(text[..separator]);
            Names.Validate(name, line);
            string content = text[(separator + 1)..];
            if (!preserveSpecialCharacters)
            {
                content = content.Replace("<#CR>", "\r\n", StringComparison.OrdinalIgnoreCase)
                    .Replace("<#TAB>", "\t", StringComparison.OrdinalIgnoreCase)
                    .Replace("<#DBL>", "\"", StringComparison.OrdinalIgnoreCase);
            }

            if (currentModuleFields is { } fields)
            {
                if (fields.Any(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw Error($"宣言が重複しています: {name}", line);
                }

                fields.Add(new(line, name, ModuleFieldKind.Constant,
                    new LiteralExpr(Value.String(content)), [], [], false, sourcePath)
                {
                    SourceOrder = position
                });
            }
            else
            {
                if (globals.Any(global => global.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw Error($"宣言が重複しています: {name}", line);
                }

                globals.Add(new(line, name, new LiteralExpr(Value.String(content)), true)
                {
                    SourcePath = sourcePath,
                    SourceOrder = position
                });
            }

            return new FlowStatement(line, FlowKind.Normal, 0);
        }

        private Statement ParseIfStatement(
            int line, string word, string rest, int loops)
        {
            int then = Find(rest, Keywords.Then);
            if (then >= 0 && rest[(then
                + Keywords.Then.Length)..].Trim().Length > 0)
            {
                if (word == Keywords.IfB)
                {
                    throw Error(
                        "IFBにはブロック形式を使用してください。", line);
                }

                string inline = rest[(then + Keywords.Then.Length)..];
                int otherwise = Find(inline,
                    Keywords.Else);
                string yes = otherwise < 0 ? inline
                    : inline[..otherwise];
                var branches = new List<IfBranch>
                {
                    new(ParseExpression(
                        rest[..then], line, true), Array.AsReadOnly([Simple(line,
                        yes.Trim(), loops)]))
                };
                if (otherwise >= 0)
                {
                    branches.Add(new(null,
                        Array.AsReadOnly([Simple(line, inline[(otherwise
                        + Keywords.Else.Length)..].Trim(), loops)])));
                }

                return new IfStatement(line,
                    branches.AsReadOnly());
            }

            Expr condition = ParseExpression(
                WithoutThen(rest, line), line, true);
            var choices = new List<IfBranch>
            {
                new(condition, Block(loops, false,
                    Keywords.ElseIf, Keywords.Else, Keywords.EndIf))
            };
            while (Keyword == Keywords.ElseIf)
            {
                (int Line, string Text) branch = source[position++];
                choices.Add(new(ParseExpression(
                    WithoutThen(Rest(branch.Text), branch.Line), branch.Line, true),
                    Block(loops, false, Keywords.ElseIf, Keywords.Else,
                    Keywords.EndIf), branch.Line));
            }

            if (Keyword == Keywords.Else)
            {
                End(Keywords.Else, line);
                choices.Add(new(null, Block(loops,
                    false, Keywords.EndIf)));
            }

            End(Keywords.EndIf, line);
            return new IfStatement(line,
                choices.AsReadOnly());
        }

        private Statement ParseForStatement(
            int line, string rest, int loops)
        {
            int inside = Find(rest, Keywords.In);
            if (inside >= 0)
            {
                string name = rest[..inside].Trim();
                Names.Validate(name, line);
                Expr collection = ParseExpression(
                    rest[(inside + Keywords.In.Length)..], line);
                IReadOnlyList<Statement> body = Block(
                    loops + 1, false, Keywords.Next);
                End(Keywords.Next, line);
                return new ForInStatement(line,
                    name, collection, body);
            }

            int equal = Find(rest, "=");
            int to = Find(rest, Keywords.To);
            int step = Find(rest, Keywords.Step);
            if (equal < 1 || to <= equal || (step
                >= 0 && step <= to))
            {
                throw Error(
                    "FOR 変数 = 開始 TO 終了 [STEP 刻み]が必要です。", line);
            }

            string counter = rest[..equal].Trim();
            Names.Validate(counter, line);
            Expr initial = ParseExpression(
                rest[(equal + 1)..to], line);
            Expr limit = ParseExpression(rest[(to
                + Keywords.To.Length)..(step < 0 ? rest.Length : step)], line);
            Expr increment = step < 0
                ? new LiteralExpr(Value.Numeric(1)) : ParseExpression(
                rest[(step + Keywords.Step.Length)..], line);
            IReadOnlyList<Statement> statements = Block(
                loops + 1, false, Keywords.Next);
            End(Keywords.Next, line);
            return new ForStatement(line, counter,
                initial, limit, increment, statements);
        }

        private Statement ParseSelectStatement(
            int line, string rest, int loops)
        {
            Expr selector = ParseExpression(rest, line);
            var cases = new List<CaseBranch>();
            bool hasDefault = false;
            if (position < source.Length
                && Keyword is not (Keywords.Case
                    or Keywords.Default or Keywords.SelEnd))
            {
                throw Error(
                    "SELECTの直後にはCASE・DEFAULT・SELENDを指定してください。",
                    source[position].Line);
            }

            while (Keyword is Keywords.Case or Keywords.Default)
            {
                (int Line, string Text) branch = source[position++];
                if (hasDefault)
                {
                    throw Error(
                        "DEFAULTは最後に一度だけ指定してください。", branch.Line);
                }

                IReadOnlyList<Expr>? values = null;
                if (Word(branch.Text) == Keywords.Case)
                {
                    values = Split(Rest(
                        branch.Text), ',').Select(s => ParseExpression(s,
                        branch.Line)).ToList().AsReadOnly();
                }
                else
                {
                    RequireEmpty(Rest(branch.Text),
                        branch.Line);
                    hasDefault = true;
                }

                cases.Add(new(values, Block(loops,
                    false, Keywords.Case, Keywords.Default, Keywords.SelEnd),
                    branch.Line));
            }

            End(Keywords.SelEnd, line);
            return new SelectStatement(line,
                selector, cases.AsReadOnly());
        }

        private Statement ParseTryStatement(
            int line, string rest, int loops)
        {
            RequireEmpty(rest, line);
            IReadOnlyList<Statement> body = Block(
                loops, false, Keywords.Finally, Keywords.Except,
                Keywords.EndTry);
            if (Keyword is not (Keywords.Finally or Keywords.Except))
            {
                throw Error(
                    "TRYにはFINALLYまたはEXCEPTが必要です。", line);
            }

            bool finallyMode = Keyword
                == Keywords.Finally;
            End(Keyword, line);
            IReadOnlyList<Statement> handler = Block(
                loops, false, Keywords.EndTry);
            End(Keywords.EndTry, line);
            return new TryStatement(line, body,
                handler, finallyMode);
        }

        private Statement ParseGlobalDeclarationStatement(
            int line, string word, string rest, bool topLevel)
        {
            if (!topLevel && word == Keywords.Public)
            {
                throw Error(
                    "現在PUBLICはメインの直下でのみ宣言できます。", line);
            }

            bool hash = word == Keywords.Public && Word(rest) == Keywords.HashTbl;
            if (hash)
            {
                rest = Rest(rest);
            }

            if (word == Keywords.Public)
            {
                return ParsePublicDeclaration(rest, line, hash);
            }

            int equal = Find(rest, "=");
            string name = equal < 0 ? rest
                : rest[..equal].Trim();
            Names.Validate(name, line);
            if (currentModuleFields is null
                ? globals.Any(g => g.Name.Equals(name,
                    StringComparison.OrdinalIgnoreCase))
                : currentModuleFields.Any(field => field.Name.Equals(name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw Error($"宣言が重複しています: {name}", line);
            }

            if (word == Keywords.Const && equal < 0)
            {
                throw Error("CONSTには初期値が必要です。", line);
            }

            Expr? value = equal < 0 ? null
                : ParseExpression(rest[(equal + 1)..], line);
            if (value is not null && !IsConstantInitializer(value))
            {
                throw Error(
                    "初期値に使用できない式です。グローバル初期値には定数・先行宣言・組み込み関数を指定してください。", line);
            }

            if (hash)
            {
                value = new HashCreateExpr(value);
            }

            if (currentModuleFields is not null)
            {
                currentModuleFields.Add(new(line, name,
                    ModuleFieldKind.Constant, value, [], [], false,
                    sourcePath)
                {
                    SourceOrder = position
                });
                return new FlowStatement(line, FlowKind.Normal, 0);
            }

            globals.Add(new(line, name, value, word
                == Keywords.Const)
            {
                SourcePath = sourcePath,
                SourceOrder = position
            });
            return new FlowStatement(line,
                FlowKind.Normal, 0);
        }

        private Statement Simple(int line, string text,
            int loops)
        {
            string word = Word(text);
            string rest = Rest(text);
            if (word is Keywords.ComErrIgn or Keywords.ComErrRet)
            {
                RequireEmpty(rest, line);
                return new ComErrorControlStatement(line,
                    word == Keywords.ComErrIgn);
            }

            if (word == Keywords.DefDll)
            {
                return ParseNativeDllDeclaration(line, rest);
            }

            if (word == Keywords.Thread)
            {
                Expr call = ParseExpression(rest, line);
                if (call is not (CallExpr or MemberExpr))
                {
                    throw Error("THREADには関数呼出しを指定してください。", line);
                }

                return new ThreadStatement(line, call)
                {
                    SourcePath = sourcePath
                };
            }

            if (word == Keywords.Option)
            {
                return ParseRuntimeOptions(rest, line);
            }

            if (word == Keywords.Print)
            {
                return new PrintStatement(line,
                    rest.Length == 0 ? new LiteralExpr(Value.String(""))
                        : ParseExpression(rest, line));
            }

            if (word == Keywords.HashTbl)
            {
                int hashEqual = Find(rest, "=");
                string name = hashEqual < 0 ? rest : rest[..hashEqual].Trim();
                Names.Validate(name, line);
                return new HashDeclareStatement(line, name,
                    new HashCreateExpr(hashEqual < 0 ? null
                        : ParseExpression(rest[(hashEqual + 1)..], line)));
            }

            if (word == Keywords.Call)
            {
                Match match = Regex.Match(rest,
                    "^(?:\"([^\"]+)\"|'([^']+)'|((?!['\"])[^\"()]+?))\\s*(?:\\((.*)\\))?$",
                    RegexOptions.Singleline);
                if (!match.Success)
                {
                    throw Error("CALLには固定ファイル名を指定してください。", line);
                }

                string name = match.Groups[1].Success ? match.Groups[1].Value
                    : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value.Trim();
                IReadOnlyList<Expr> arguments = ParseCallArguments(
                    match.Groups[4].Value, line);
                if (arguments.Any(argument => argument is OmittedExpr))
                {
                    throw Error("CALLの引数は省略できません。", line);
                }

                return new ExternalCallStatement(line,
                    compilation.Load(name, sourcePath, line, preserveSpecialCharacters), arguments);
            }

            if (word is Keywords.Break or Keywords.Continue)
            {
                int levels = 1;
                if (rest.Length > 0 && !TryParseLoopLevels(rest,
                    out levels))
                {
                    throw Error("段数には正の整数リテラルを指定してください。", line);
                }

                if (levels < 1 || levels > loops)
                {
                    throw Error(
                        "BREAK/CONTINUEの段数がループの深さを超えています。", line);
                }

                return new FlowStatement(line, word
                    == Keywords.Break ? FlowKind.Break : FlowKind.Continue, levels);
            }

            if (word is Keywords.Exit or Keywords.ExitExit)
            {
                if (word == Keywords.Exit)
                {
                    RequireEmpty(rest, line);
                }

                return new FlowStatement(line, word
                    == Keywords.Exit ? FlowKind.Exit : FlowKind.Stop, 0,
                    word == Keywords.ExitExit && rest.Length > 0
                        ? ParseExpression(rest, line) : null);
            }

            bool declare = word == Keywords.Dim;
            string body = declare ? rest : text;
            return declare ? ParseDimDeclarations(body, line)
                : ParseAssignment(body, line, false);
        }
    }
}
