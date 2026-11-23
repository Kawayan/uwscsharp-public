using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace UWSCSharp.Core;

public static partial class ScriptCompiler
{
    private sealed partial class Parser
    {
        private void ParseRoutine(Dictionary<string, Routine> destination,
            string? moduleName = null)
        {
            bool returnsValue = Keyword == Keywords.Function;
            (int Line, string Text) header = source[position++];
            string signature = Rest(header.Text);
            // ヘルプに掲載された引数括弧なしのコンストラクタも受け付けます。
            if (moduleName is not null && signature.Equals(moduleName,
                StringComparison.OrdinalIgnoreCase))
            {
                signature += "()";
            }

            Match match = IdentifierPatterns.Routine().Match(signature);
            if (!match.Success)
            {
                throw Error("関数定義は名前(引数)を指定してください。",
                    header.Line);
            }

            string name = match.Groups[1].Value;
            Names.Validate(name, header.Line);
            if (destination.ContainsKey(name))
            {
                throw Error($"関数名が重複しています: {name}",
                    header.Line);
            }

            var parameters = new List<Parameter>();
            var names = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            bool optional = false;
            if (match.Groups[2].Value.Trim().Length > 0)
            {
                foreach (string raw in Split(
                    match.Groups[2].Value, ','))
                {
                    parameters.Add(ParseParameter(raw, header.Line,
                        names, ref optional));
                }
            }

            if (moduleName is not null && name.Equals(moduleName,
                StringComparison.OrdinalIgnoreCase) && parameters.Count > 0)
            {
                throw Error("コンストラクタに引数は指定できません。",
                    header.Line);
            }

            IReadOnlyList<Statement> body = Block(0,
                false, Keywords.Fend);
            End(Keywords.Fend, header.Line);
            destination.Add(name, new(header.Line, name,
                returnsValue, parameters.AsReadOnly(), body));
        }

        private void ParseModule()
        {
            string closing = Keyword == Keywords.Module
                ? Keywords.EndModule : Keywords.EndClass;
            (int line, string text) = source[position++];
            string name = Rest(text);
            Names.Validate(name, line);
            if (modules.Any(module => module.Name.Equals(name,
                StringComparison.OrdinalIgnoreCase)))
            {
                throw Error($"モジュール名が重複しています: {name}", line);
            }

            var fields = new List<ModuleFieldDefinition>();
            var nativeDlls = new List<NativeDllDeclarationStatement>();
            var members = new Dictionary<string, Routine>(
                StringComparer.OrdinalIgnoreCase);
            currentModuleFields = fields;
            currentModuleNativeDlls = nativeDlls;
            while (position < source.Length && Keyword != closing)
            {
                token.ThrowIfCancellationRequested();
                if (Keyword is Keywords.Function or Keywords.Procedure)
                {
                    ParseRoutine(members, name);
                    continue;
                }

                if (Keyword == Keywords.TextBlock)
                {
                    (int blockLine, string blockText) = source[position++];
                    ParseStatement(blockLine, blockText, 0, true);
                    continue;
                }

                if (Keyword == Keywords.DefDll)
                {
                    (int dllLine, string dllText) = source[position++];
                    ParseNativeDllDeclaration(dllLine, Rest(dllText));
                    continue;
                }

                ModuleFieldKind kind = Keyword switch
                {
                    Keywords.Dim => ModuleFieldKind.Private,
                    Keywords.Public => ModuleFieldKind.Public,
                    Keywords.Const => ModuleFieldKind.Constant,
                    _ => throw Error("モジュールの直下には変数・定数・関数を宣言してください。")
                };
                (int fieldLine, string fieldText) = source[position++];
                string declaration = Rest(fieldText);
                if (kind == ModuleFieldKind.Constant && Find(declaration, "=") < 0)
                {
                    throw Error("CONSTには初期値が必要です。", fieldLine);
                }

                Statement parsed = kind == ModuleFieldKind.Constant
                    ? ParseAssignment(declaration, fieldLine, true)
                    : ParseDimDeclarations(declaration, fieldLine);
                IReadOnlyList<Statement> declarations = parsed
                    is DeclarationGroupStatement group
                    ? group.Declarations : [parsed];
                foreach (Statement statement in declarations)
                {
                    ModuleFieldDefinition field = statement switch
                    {
                        AssignStatement scalar => new(fieldLine,
                            scalar.Name, kind, scalar.Expression,
                            [], [], false, sourcePath),
                        ArrayDeclareStatement array when kind != ModuleFieldKind.Constant =>
                            new(fieldLine, array.Name, kind, null,
                                array.Bounds, array.Initializers,
                                array.Infer, sourcePath),
                        _ => throw Error("モジュールの宣言が不正です。", fieldLine)
                    };
                    field = field with
                    {
                        SourceOrder = position
                    };
                    if (kind == ModuleFieldKind.Constant && field.Initializer is null)
                    {
                        throw Error("CONSTには初期値が必要です。", fieldLine);
                    }

                    if (fields.Any(existing => existing.Name.Equals(field.Name,
                        StringComparison.OrdinalIgnoreCase)))
                    {
                        throw Error($"宣言が重複しています: {field.Name}", fieldLine);
                    }

                    fields.Add(field);
                }
            }

            End(closing, line);
            currentModuleFields = null;
            currentModuleNativeDlls = null;
            modules.Add(new(line, name, fields.AsReadOnly(),
                new ReadOnlyDictionary<string, Routine>(members), sourcePath,
                nativeDlls.AsReadOnly()));
        }

        private NativeDllDeclarationStatement ParseNativeDllDeclaration(
            int line, string text)
        {
            var statement = new NativeDllDeclarationStatement(line,
                NativeDllDeclarationParser.Parse(text, line, token))
            {
                SourcePath = sourcePath
            };
            (currentModuleNativeDlls ?? nativeDllDeclarations).Add(statement);
            return statement;
        }

        private Parameter ParseParameter(string raw, int line,
            HashSet<string> names, ref bool optional)
        {
            string parameter = raw.Trim();
            bool byReference = Word(parameter)
                == Keywords.Var;
            if (byReference)
            {
                parameter = Rest(parameter);
            }

            int equal = Find(parameter, "=");
            string parameterName = equal < 0
                ? parameter : parameter[..equal].Trim();
            int arrayRank = 0;
            Match arrayParameter = IdentifierPatterns.ArrayParameter().Match(parameterName);
            if (arrayParameter.Success)
            {
                parameterName = arrayParameter.Groups[1].Value;
                arrayRank = arrayParameter.Groups[2].Value.Count(
                    c => c == '[');
            }

            Names.Validate(parameterName,
                line);
            if (parameterName.Equals(BuiltinVariableNames.Result,
                StringComparison.OrdinalIgnoreCase) || !names.Add(
                parameterName))
            {
                throw Error(
                    "仮引数名が重複しているかRESULTです。", line);
            }

            Expr? defaultValue = equal < 0
                ? null : ParseExpression(parameter[(equal + 1)..], line);
            if (optional && defaultValue is null)
            {
                throw Error(
                    "既定引数の後に必須引数を指定できません。", line);
            }

            if (defaultValue is not null
                && !IsDefaultValueExpression(defaultValue))
            {
                throw Error(
                    "引数の既定値には値・名前・添字・演算式を指定してください。",
                    line);
            }

            optional |= defaultValue is not null;
            return new(parameterName,
                byReference, defaultValue, arrayRank);
        }

        private Statement ParsePublicDeclaration(string body, int line,
            bool hash)
        {
            Statement parsed = ParseDimDeclarations(body, line);
            IReadOnlyList<Statement> declarations = parsed
                is DeclarationGroupStatement group
                ? group.Declarations : [parsed];
            foreach (Statement declaration in declarations)
            {
                AddPublicDeclaration(declaration, line, hash);
            }

            return new FlowStatement(line, FlowKind.Normal, 0);
        }

        private void AddPublicDeclaration(Statement parsed, int line,
            bool hash)
        {
            GlobalDeclaration declaration = parsed switch
            {
                AssignStatement scalar => new(line, scalar.Name,
                    hash ? new HashCreateExpr(scalar.Expression)
                        : scalar.Expression, false)
                {
                    Kind = hash ? GlobalDeclarationKind.HashTable
                        : GlobalDeclarationKind.Scalar
                },
                ArrayDeclareStatement array when !hash => new(line,
                    array.Name, null, false)
                {
                    Kind = GlobalDeclarationKind.Array,
                    Bounds = array.Bounds,
                    Initializers = array.Initializers,
                    Infer = array.Infer
                },
                _ => throw Error("PUBLICの宣言が不正です。", line)
            };

            IEnumerable<Expr> expressions = declaration.Bounds
                .Concat(declaration.Initializers);
            if (parsed is AssignStatement { Expression: not null } assignment)
            {
                expressions = expressions.Append(assignment.Expression);
            }

            if (expressions.Any(expression => !IsConstantInitializer(expression)))
            {
                throw Error(
                    "初期値に使用できない式です。グローバル初期値には定数・先行宣言・組み込み関数を指定してください。",
                    line);
            }

            GlobalDeclaration? previous = globals.FirstOrDefault(existing =>
                existing.Name.Equals(declaration.Name, StringComparison.OrdinalIgnoreCase));
            if (previous is not null && (optPublic || previous.Constant
                || previous.Kind != declaration.Kind
                || declaration.Kind == GlobalDeclarationKind.HashTable))
            {
                throw Error($"宣言が重複しています: {declaration.Name}", line);
            }

            globals.Add(declaration with
            {
                SourcePath = sourcePath,
                SourceOrder = position
            });
        }

        private Statement ParseDimDeclarations(string body, int line)
        {
            var declarations = new List<Statement>();
            string remaining = body;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int comma = Find(remaining, ",");
                string item = (comma < 0 ? remaining
                    : remaining[..comma]).Trim();
                int equal = Find(item, "=");
                string target = equal < 0 ? item : item[..equal].Trim();
                if (equal >= 0 && target.Contains('['))
                {
                    // 配列初期値のカンマは、従来どおり行末まで初期値を区切ります。
                    // 後続の別宣言と誤認して、変数参照や比較式の意味を変えません。
                    declarations.Add(ParseAssignment(remaining, line, true) with
                    {
                        SourcePath = sourcePath
                    });
                    break;
                }

                declarations.Add(ParseAssignment(item, line, true) with
                {
                    SourcePath = sourcePath
                });
                if (comma < 0)
                {
                    break;
                }

                remaining = remaining[(comma + 1)..].TrimStart();
            }

            return declarations.Count == 1 ? declarations[0]
                : new DeclarationGroupStatement(line,
                    declarations.AsReadOnly());
        }

        private Statement ParseAssignment(string body, int line,
            bool declare)
        {
            int equal = Find(body, "=");
            string target = equal < 0 ? body
                : body[..equal].Trim();
            if (equal >= 0 && target.Contains('.')
                && ParseExpression(target, line) is MemberExpr member)
            {
                if (declare)
                {
                    throw Error("objectへの代入先が不正です。", line);
                }

                return new MemberStatement(line, member,
                    ParseExpression(body[(equal + 1)..], line));
            }

            if ((declare || equal >= 0) && target.Contains('['))
            {
                if (ParseExpression(target,
                    line) is not IndexExpr index)
                {
                    throw Error("配列の参照が不正です。", line);
                }

                Names.Validate(index.Name, line);
                if (declare)
                {
                    List<Expr> initial = equal < 0
                        ? new List<Expr>() : [.. Split(body[(equal + 1)..],
                        ',').Select(s => ParseExpression(s, line))];
                    return new ArrayDeclareStatement(line,
                        index.Name, index.Indices, initial.AsReadOnly(),
                        index.Indices.Count == 0);
                }

                if (equal < 0)
                {
                    throw Error("配列代入には値が必要です。", line);
                }

                return new IndexAssignStatement(line, index,
                    ParseExpression(body[(equal + 1)..], line));
            }

            if (equal >= 0)
            {
                string name = body[..equal].Trim();
                Names.Validate(name, line);
                return new AssignStatement(line, name,
                    ParseExpression(body[(equal + 1)..], line), declare);
            }

            if (declare)
            {
                Names.Validate(body, line);
                return new AssignStatement(line, body, null,
                    true);
            }

            Expr expression = ParseExpression(body, line);
            if (expression is MemberExpr memberCall)
            {
                return new MemberStatement(line, memberCall, null);
            }

            if (expression is CallExpr call)
            {
                return new CallStatement(line, call);
            }

            throw Error("未対応または不正な構文です。", line);
        }
    }
}
