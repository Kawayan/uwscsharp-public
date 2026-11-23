using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UWSCSharp.Core;
/// <summary>
/// 式やホスト操作を実行せずに、対応する構文を解析します。
/// </summary>
public static partial class ScriptCompiler
{
    /// <summary>
    /// 外部CALLを読み込まず、ソースの構文を解析します。
    /// </summary>
    public static ScriptProgram CompileWithoutExternalFiles(
        string source, CancellationToken cancellationToken = default)
        => CompileWithContext(source, null, false, cancellationToken);

    /// <summary>
    /// ソースを解析し、再利用可能なスクリプトを生成します。
    /// </summary>
    public static ScriptProgram Compile(string source,
        CancellationToken cancellationToken = default)
        => CompileWithContext(source, null, true, cancellationToken);

    /// <summary>
    /// ファイルと静的CALL依存を読み込み、再利用可能なプログラムを生成します。
    /// </summary>
    public static ScriptProgram CompileFile(string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CompileWithContext(Source.ReadFile(path), path, true, cancellationToken);
    }

    internal static ScriptProgram CompileWithContext(string source,
        string? path, bool allowCalls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var compilation = new ScriptCompilation(path, allowCalls, cancellationToken);
        ExternalScript root = compilation.Register(compilation.BasePath);
        root.Program = ParseExternal(source, compilation, compilation.BasePath,
            false, cancellationToken);
        return compilation.Complete(root.Program);
    }

    internal static ScriptProgram ParseExternal(string source, ScriptCompilation compilation,
        string path, bool specialCharacters, CancellationToken token)
    {
        try
        {
            return new Parser(source, token, compilation, path, specialCharacters).Compile();
        }
        catch (ScriptException error)
        {
            error.SetSourcePath(path);
            throw;
        }
    }

    internal static ExternalCallStatement ParseDynamicCall(string text,
        ScriptCompilation compilation, string path, bool specialCharacters,
        CancellationToken token)
    {
        return new Parser(text, token, compilation, path,
            specialCharacters).ParseSingleCall();
    }

    private sealed partial class Parser
    {
        private readonly (int Line, string Text)[] source;
        private readonly CancellationToken token;
        private readonly Dictionary<string, Routine> routines = new(
            StringComparer.OrdinalIgnoreCase);
        private readonly List<GlobalDeclaration> globals = [];
        private readonly List<NativeDllDeclarationStatement> nativeDllDeclarations = [];
        private readonly List<ModuleDefinition> modules = [];
        private List<ModuleFieldDefinition>? currentModuleFields;
        private List<NativeDllDeclarationStatement>? currentModuleNativeDlls;
        private bool optPublic;
        private int position;
        private int depth;
        private bool preserveSpecialCharacters;
        private readonly ScriptCompilation compilation;
        private readonly string sourcePath;

        public Parser(string text, CancellationToken token,
            ScriptCompilation compilation, string sourcePath, bool specialCharacters)
        {
            this.token = token;
            this.compilation = compilation;
            this.sourcePath = sourcePath;
            preserveSpecialCharacters = specialCharacters;
            source = [.. Source.Statements(text, token)];
        }

        private string Keyword => position < source.Length
            ? Word(source[position].Text) : "";

        private int Line => position < source.Length
            ? source[position].Line : source.Length > 0
            ? source[^1].Line : 1;

        private ScriptException Error(string message,
            int? line = null) => new(line ?? Line, message);

        internal ExternalCallStatement ParseSingleCall()
        {
            if (source.Length != 1 || Keyword != Keywords.Call)
            {
                throw Error("EVALのCALLにはCALL文を1個指定してください。", 1);
            }

            position = 1;
            var statement = (ExternalCallStatement)Simple(1, source[0].Text, 0);
            return statement with
            {
                SourcePath = sourcePath
            };
        }

        /// <summary>
        /// ソースを解析し、再利用可能なスクリプトを生成します。
        /// </summary>
        public ScriptProgram Compile()
        {
            bool optFinally = false;
            bool sameString = false;
            bool shortCircuit = false;
            bool explicitVariables = false;
            bool declaresSpecialCharacters = false;
            var logOptions = new List<(int Line, string Text)>();
            while (Keyword == Keywords.Option)
            {
                (int Line, string Text) option = source[position++];
                foreach (string name in SplitOptions(Rest(option.Text)))
                {
                    int equal = Find(name, "=");
                    if ((equal >= 0 && (IsLogOption(name[..equal].Trim())
                        || IsDialogOption(name[..equal].Trim()) || IsFlagOption(name[..equal].Trim())))
                        || (equal < 0 && IsFlagOption(name.Trim())))
                    {
                        logOptions.Add((option.Line, name));
                        continue;
                    }

                    switch (name.Trim().ToUpperInvariant())
                    {
                        case Keywords.OptFinally:
                            optFinally = true;
                            break;
                        case Keywords.OptPublic:
                            optPublic = true;
                            break;
                        case Keywords.Explicit:
                            explicitVariables = true;
                            break;
                        case Keywords.ShortCircuit:
                            shortCircuit = true;
                            break;
                        case Keywords.SameStr:
                            sameString = true;
                            break;
                        case Keywords.SpecialChar:
                            preserveSpecialCharacters = true;
                            declaresSpecialCharacters = true;
                            break;
                        default:
                            throw Error("未対応のOPTIONです。", option.Line);
                    }
                }
            }

            compilation.SetSpecialCharacterPolicy(sourcePath,
                preserveSpecialCharacters, declaresSpecialCharacters);
            var main = new List<Statement>();
            foreach ((int line, string text) in logOptions)
            {
                main.Add(ParseRuntimeOptions(text, line) with
                {
                    SourcePath = sourcePath
                });
            }

            main.AddRange(Block(0, true, Keywords.Function,
                Keywords.Procedure, Keywords.Module, Keywords.Class));
            while (position < source.Length)
            {
                token.ThrowIfCancellationRequested();
                if (Keyword is Keywords.Module or Keywords.Class)
                {
                    ParseModule();
                }
                else if (Keyword is Keywords.Function or Keywords.Procedure)
                {
                    ParseRoutine(routines);
                }
                else if (Keyword is Keywords.Public or Keywords.Const or Keywords.TextBlock)
                {
                    (int line, string text) = source[position++];
                    ParseStatement(line, text, 0, true);
                }
                else
                {
                    throw Error("関数定義の後にメイン処理を書くことはできません。");
                }
            }

            return new(main.AsReadOnly(),
                new ReadOnlyDictionary<string, Routine>(routines),
                globals.AsReadOnly(), optFinally, sameString,
                explicitVariables, modules.AsReadOnly(), optPublic)
            {
                ShortCircuit = shortCircuit,
                NativeDllDeclarations = nativeDllDeclarations.AsReadOnly(),
                ContainsTextBlocks = source.Any(item => Word(item.Text) == Keywords.TextBlock)
            };
        }

        private void End(string expected, int openingLine)
        {
            if (Keyword != expected)
            {
                throw Error($"{expected}がありません。", openingLine);
            }

            RequireEmpty(Rest(source[position].Text),
                source[position].Line);
            position++;
        }

        private static void RequireEmpty(string text, int line)
        {
            if (text.Length > 0)
            {
                throw new ScriptException(line,
                    "この文の末尾に引数は指定できません。");
            }
        }

        private static string WithoutThen(string condition,
            int line)
        {
            int then = Find(condition, Keywords.Then);
            if (then < 0)
            {
                return condition;
            }

            if (condition[(then + Keywords.Then.Length)..].Trim().Length > 0)
            {
                throw new ScriptException(line, "THENの後が不正です。");
            }

            return condition[..then];
        }

        private bool IsConstantInitializer(Expr expression) => expression switch
        {
            LiteralExpr or OmittedExpr => true,
            NameExpr => true,
            UnaryExpr unary => IsConstantInitializer(unary.Operand),
            BinaryExpr binary => IsConstantInitializer(binary.Left)
                && IsConstantInitializer(binary.Right),
            CallExpr call => BuiltinFunctions.IsBuiltin(call.Name)
                && call.Arguments.All(IsConstantInitializer),
            _ => false
        };

        private static bool IsDefaultValueExpression(
            Expr expression) => expression switch
            {
                LiteralExpr or NameExpr => true,
                IndexExpr index => index.Indices.All(IsDefaultValueExpression),
                UnaryExpr unary => IsDefaultValueExpression(
                    unary.Operand),
                BinaryExpr binary => IsDefaultValueExpression(
                    binary.Left) && IsDefaultValueExpression(binary.Right),
                _ => false
            };

        private static bool TryParseLoopLevels(string text, out int levels)
        {
            Match hexadecimal = Regex.Match(text,
                @"^([+-]?)(?:\$|0[xX]|[xX])([0-9A-Fa-f]+)$");
            if (hexadecimal.Success)
            {
                levels = 0;
                if (hexadecimal.Groups[1].Value == "-"
                    || !uint.TryParse(hexadecimal.Groups[2].Value,
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out uint magnitude)
                    || magnitude > int.MaxValue)
                {
                    return false;
                }

                levels = (int)magnitude;
                return true;
            }

            return int.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out levels);
        }

        private IReadOnlyList<Expr> ParseCallArguments(string text, int line)
        {
            try
            {
                // 除去した仮の関数名1トークン分を上限判定に残します。
                return new Expression("(" + text + ")", preserveSpecialCharacters,
                    reservedTokens: 1).ParseCallArguments();
            }
            catch (Exception ex) when (ScriptErrors.IsScriptError(ex))
            {
                throw new ScriptException(line, ex.Message);
            }
        }

        private Expr ParseExpression(string text,
            int line, bool condition = false)
        {
            try
            {
                return new Expression(text, preserveSpecialCharacters, preserveGrouping: condition).ParseExpression();
            }
            catch (Exception ex) when (ScriptErrors.IsScriptError(ex))
            {
                throw new ScriptException(line, ex.Message);
            }
        }
    }


}
