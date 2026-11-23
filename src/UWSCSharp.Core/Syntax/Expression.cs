using System.Globalization;
using System.Text.RegularExpressions;

namespace UWSCSharp.Core;

internal sealed class Expression
{
    private readonly List<string> tokens = [];
    private int position;
    private int depth;
    private readonly bool preserveSpecialCharacters;
    private readonly bool allowAssignment;
    private readonly bool preserveGrouping;
    private static readonly Regex Token = new(
        "\\G(?:\\$[0-9A-Fa-f]+|(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?|[\\p{L}_][\\p{L}\\p{N}_]*|>=|<=|<>|[()\\[\\],.:+*/!=<>-])",
        RegexOptions.CultureInvariant);

    public Expression(string text, bool preserveSpecialCharacters = false,
        int reservedTokens = 0, bool allowAssignment = false,
        CancellationToken cancellationToken = default, bool preserveGrouping = false,
        int maximumTokens = ScriptLimits.MaxExpressionTokens)
    {
        this.preserveSpecialCharacters = preserveSpecialCharacters;
        this.allowAssignment = allowAssignment;
        this.preserveGrouping = preserveGrouping;
        for (int i = 0; i < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '"')
            {
                if (!StringLiterals.TryReadEnd(text, i, out int end,
                    cancellationToken))
                {
                    throw new InvalidOperationException("文字列が閉じていません。");
                }

                tokens.Add(text[i..end]);
                i = end;
            }
            else
            {
                Match match = Token.Match(text, i);
                if (!match.Success)
                {
                    throw new InvalidOperationException(
                        $"未対応または不正な文字: {text[i]}");
                }

                tokens.Add(match.Value);
                i += match.Length;
            }

            if (tokens.Count + reservedTokens > maximumTokens)
            {
                throw new InvalidOperationException("式が長すぎます。");
            }
        }
    }

    private string Peek => position < tokens.Count
        ? tokens[position].ToUpperInvariant() : "";

    private string Take() => position < tokens.Count
        ? tokens[position++] : throw new InvalidOperationException(
        "式が途中で終わっています。");

    private static int Precedence(string op) => op switch
    {
        Keywords.Or or Keywords.Xor => 1,
        Keywords.And => 2,
        "=" or "<>" or "<" or ">" or "<=" or ">=" => 3,
        "+" or "-" => 4,
        "*" or "/" or Keywords.Mod => 5,
        _ => 0
    };

    public Expr ParseExpression()
    {
        Expr value = Parse(1);
        if (position != tokens.Count)
        {
            throw new InvalidOperationException(
                $"式の末尾が不正です: {tokens[position]}");
        }

        return value;
    }

    internal IReadOnlyList<Expr> ParseCallArguments()
    {
        // CALLでも従来の外側の呼出しに相当する深さを確保します。
        depth = 1;
        IReadOnlyList<Expr> arguments = ParseArguments(false);
        if (position != tokens.Count)
        {
            throw new InvalidOperationException(
                $"式の末尾が不正です: {tokens[position]}");
        }

        return arguments;
    }

    private IReadOnlyList<Expr> ParseArguments(bool memberArguments,
        bool shortcutKeys = false)
    {
        Take();
        List<Expr> arguments = [];
        HashSet<string>? argumentNames = memberArguments
            ? new(StringComparer.OrdinalIgnoreCase) : null;
        if (Peek != ")")
        {
            while (true)
            {
                if (shortcutKeys && arguments.Count > 0
                    && position + 1 < tokens.Count
                    && tokens[position].Length == 1
                    && char.IsAsciiLetterOrDigit(tokens[position][0])
                    && tokens[position + 1] is "," or ")")
                {
                    // 単独の英数字だけを、原版の引数前処理と同じVK名へ展開します。
                    string key = Take().ToUpperInvariant();
                    arguments.Add(new NameExpr("VK_" + key));
                }
                else
                {
                    arguments.Add(ParseArgument(memberArguments, argumentNames));
                }
                if (Peek != ",")
                {
                    break;
                }

                Take();
            }
        }

        if (Take() != ")")
        {
            throw new InvalidOperationException("閉じ括弧が必要です。");
        }

        return arguments.AsReadOnly();
    }

    private Expr ParseArgument(bool memberArguments,
        HashSet<string>? argumentNames)
    {
        if (!memberArguments)
        {
            return Peek is "," or ")" ? new OmittedExpr() : Parse(1);
        }

        bool byReference = Peek == Keywords.Var;
        if (byReference)
        {
            Take();
        }

        string? argumentName = null;
        if (position + 1 < tokens.Count
            && IdentifierPatterns.Full().IsMatch(tokens[position])
            && tokens[position + 1] is "=" or ":")
        {
            argumentName = Take();
            if (Peek == ":")
            {
                Take();
            }

            if (Take() != "=" || !argumentNames!.Add(argumentName))
            {
                throw new InvalidOperationException("COM引数名が不正または重複しています。");
            }

            if (!byReference && Peek == Keywords.Var)
            {
                Take();
                byReference = true;
            }
        }

        Expr argument = Peek is "," or ")"
            ? new OmittedExpr() : Parse(1);
        if (byReference)
        {
            if (argument is not (NameExpr or IndexExpr or MemberExpr))
            {
                throw new InvalidOperationException("VARには書込み可能な参照を指定してください。");
            }

            argument = new ReferenceExpr(argument);
        }

        if (argumentName is not null)
        {
            argument = new NamedExpr(argumentName, argument);
        }

        return argument;
    }

    private Expr Parse(int minimum)
    {
        Expr left = Primary();
        while (Precedence(Peek) >= minimum)
        {
            string op = Take().ToUpperInvariant();
            left = new BinaryExpr(op, left, Parse(
                Precedence(op) + 1));
        }

        if (minimum == 1 && allowAssignment && Peek == ":")
        {
            Take();
            if (Take() != "=" || left is not (NameExpr or IndexExpr or MemberExpr))
            {
                throw new InvalidOperationException("EVALの代入先が不正です。");
            }

            return new EvaluatedAssignmentExpr(left, Parse(1));
        }

        return left;
    }

    private Expr Primary()
    {
        if (++depth > ScriptLimits.MaxExpressionNesting)
        {
            throw new InvalidOperationException("式の入れ子が深すぎます。");
        }

        try
        {
            Expr value = PrimaryCore();
            while (Peek == ".")
            {
                Take();
                string name = Take();
                if (!IdentifierPatterns.Full().IsMatch(name))
                {
                    throw new InvalidOperationException("member名が不正です。");
                }

                IReadOnlyList<Expr> arguments = Peek == "("
                    ? ParseArguments(true) : [];

                value = new MemberExpr(value, name, arguments);
                if (Peek == "[")
                {
                    if (arguments.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "関数の結果へ直接添字を指定できません。");
                    }

                    var indices = new List<Expr>();
                    bool empty = false;
                    while (Peek == "[")
                    {
                        if (empty)
                        {
                            throw new InvalidOperationException(
                                "空の添字と別の添字は併用できません。");
                        }

                        Take();
                        empty = Peek == "]";
                        if (empty && indices.Count > 0)
                        {
                            throw new InvalidOperationException(
                                "空の添字と別の添字は併用できません。");
                        }

                        if (!empty)
                        {
                            do
                            {
                                indices.Add(Parse(1));
                                if (Peek != ",")
                                {
                                    break;
                                }

                                Take();
                            } while (true);
                        }

                        if (Take() != "]")
                        {
                            throw new InvalidOperationException(
                                "閉じ角括弧が必要です。");
                        }
                    }

                    value = new MemberExpr(((MemberExpr)value).Target,
                        name, indices.AsReadOnly())
                    {
                        Indexed = true
                    };
                }
            }

            return value;
        }
        finally
        {
            depth--;
        }
    }

    private Expr PrimaryCore()
    {
        string token = Take();
        if (token == ".")
        {
            position--;
            return new WithTargetExpr();
        }

        if (token is "+" or "-" or "!")
        {
            return new UnaryExpr(token, Primary());
        }

        if (token == "(")
        {
            Expr value = Parse(1);
            if (Take() != ")")
            {
                throw new InvalidOperationException(
                    "閉じ括弧が必要です。");
            }

            return preserveGrouping ? new GroupedExpr(value) : value;
        }

        if (token.StartsWith('"'))
        {
            if (preserveSpecialCharacters)
            {
                return new LiteralExpr(Value.String(token[1..^1]));
            }

            return new LiteralExpr(Value.String(
                token[1..^1].Replace("<#CR>", "\r\n",
                StringComparison.OrdinalIgnoreCase).Replace("<#TAB>", "\t",
                StringComparison.OrdinalIgnoreCase).Replace("<#DBL>", "\"",
                StringComparison.OrdinalIgnoreCase)));
        }

        if (token.StartsWith('$'))
        {
            if (!long.TryParse(token.AsSpan(1),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out long hexadecimal))
            {
                throw new InvalidOperationException(
                    "16進数が64bit範囲外です。");
            }

            // 最上位ビットを符号として解釈した後でdoubleへ変換する。
            return new LiteralExpr(Value.Numeric(hexadecimal));
        }

        if (double.TryParse(token, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double number))
        {
            return new LiteralExpr(Value.Numeric(number));
        }

        if (!IdentifierPatterns.Full().IsMatch(token))
        {
            throw new InvalidOperationException(
                $"式の要素が不正です: {token}");
        }

        if (Peek == "(")
        {
            bool shortcutKeys = token.Equals(BuiltinFunctionNames.ScKey,
                StringComparison.OrdinalIgnoreCase);
            return new CallExpr(token, ParseArguments(false, shortcutKeys))
            {
                PreserveSpecialCharacters = preserveSpecialCharacters
            };
        }

        if (Peek == "[")
        {
            var indices = new List<Expr>();
            bool empty = false;
            while (Peek == "[")
            {
                if (empty)
                {
                    throw new InvalidOperationException(
                        "空の添字と別の添字は併用できません。");
                }

                Take();
                if (Peek == "]")
                {
                    if (indices.Count != 0)
                    {
                        throw new InvalidOperationException(
                            "空の添字と別の添字は併用できません。");
                    }

                    empty = true;
                }

                if (Peek != "]")
                {
                    while (true)
                    {
                        indices.Add(Parse(1));
                        if (Peek != ",")
                        {
                            break;
                        }

                        Take();
                    }
                }

                if (Take() != "]")
                {
                    throw new InvalidOperationException(
                        "閉じ角括弧が必要です。");
                }
            }

            return new IndexExpr(token, indices.AsReadOnly());
        }

        return token.ToUpperInvariant() switch
        {
            Keywords.True => new LiteralExpr(Value.Logical(true)),
            Keywords.False => new LiteralExpr(Value.Logical(false)),
            Keywords.Empty => new LiteralExpr(Value.Empty),
            Keywords.Null => new LiteralExpr(new(
                ValueKind.Null)),
            Keywords.Nothing => new LiteralExpr(Value.Nothing),
            _ => new NameExpr(token)
        };
    }

    internal static long Integer(Value value)
    {
        double number = Math.Truncate(NumericOperations.Number(value));
        if (number < long.MinValue || number
            >= 9223372036854775808d)
        {
            throw new InvalidOperationException(
                "整数演算の範囲を超えました。");
        }

        return (long)number;
    }

    private static string ConcatenationText(Value value)
    {
        // 連結に使う数値にも、他の数値演算と同じ有限性を要求します。
        if (value.Kind == ValueKind.Number)
        {
            _ = value.AsNumber();
        }

        return value.Display();
    }

    internal static Value Apply(string op, Value a, Value b, bool sameString = false,
        INumericArithmetic? arithmetic = null)
    {
        if (op is "=" or "<>"
            && (a.Kind is ValueKind.Nothing or ValueKind.Object
                || b.Kind is ValueKind.Nothing or ValueKind.Object))
        {
            bool references = a.Kind is ValueKind.Nothing or ValueKind.Object
                && b.Kind is ValueKind.Nothing or ValueKind.Object;
            bool equal = references && ReferenceEquals(a.AsObject(), b.AsObject());
            return Value.Logical(op == "=" ? equal : !equal);
        }

        if (op == "+" && (a.Kind == ValueKind.String
            || b.Kind == ValueKind.String))
        {
            return EncodedText.Concatenate(a, b) ?? Value.String(ConcatenationText(a)
                + ConcatenationText(b));
        }

        if (op is "=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            int comparison = a.Kind == ValueKind.String
                && b.Kind == ValueKind.String
                ? (sameString ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase).Compare(a.Text, b.Text)
                : NumericOperations.Number(a).CompareTo(
                    NumericOperations.Number(b));
            bool result = op switch
            {
                "=" => comparison == 0,
                "<>" => comparison != 0,
                "<" => comparison < 0,
                ">" => comparison > 0,
                "<=" => comparison <= 0,
                _ => comparison >= 0
            };
            return Value.Logical(result);
        }

        if (op is Keywords.And or Keywords.Or or Keywords.Xor)
        {
            long x = Integer(a);
            long y = Integer(b);
            return Value.Numeric(op == Keywords.And ? x & y
                : op == Keywords.Or ? x | y : x ^ y);
        }

        double l = NumericOperations.Number(a);
        double r = NumericOperations.Number(b);
        if ((op is "/" or Keywords.Mod) && r == 0)
        {
            throw new InvalidOperationException("ゼロで除算できません。");
        }

        if (arithmetic is not null && op is "+" or "-" or "*" or "/")
        {
            NumericBinaryOperation operation = op switch
            {
                "+" => NumericBinaryOperation.Add,
                "-" => NumericBinaryOperation.Subtract,
                "*" => NumericBinaryOperation.Multiply,
                _ => NumericBinaryOperation.Divide
            };
            return Value.Numeric(arithmetic.Evaluate(operation, l, r));
        }

        return Value.Numeric(op switch
        {
            "+" => l + r,
            "-" => l - r,
            "*" => l * r,
            "/" => l / r,
            Keywords.Mod => l % r,
            _ => throw new InvalidOperationException(
                $"未対応の演算子: {op}")
        });
    }
}
