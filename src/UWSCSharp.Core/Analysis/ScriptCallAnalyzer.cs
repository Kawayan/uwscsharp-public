namespace UWSCSharp.Core;

/// <summary>
/// 式・ホストを実行せず、外部CALLを読まずに呼出しを調べます。
/// </summary>
public static class ScriptCallAnalyzer
{
    /// <summary>
    /// 構文を解析し、宣言・関数名・引数形式について警告を返します。
    /// </summary>
    public static ScriptCallAnalysis Analyze(string source,
        CancellationToken cancellationToken = default)
    {
        ScriptProgram program = ScriptCompiler
            .CompileWithoutExternalFiles(source, cancellationToken);
        var analysis = new Analysis(program, cancellationToken);
        return analysis.Run();
    }

    private sealed class Analysis(
        ScriptProgram program, CancellationToken token)
    {
        private const int DiagnosticLimit = 100;
        private readonly List<ScriptCallDiagnostic> diagnostics = [];
        private readonly ILookup<string, NativeDllDeclarationStatement> nativeDlls =
            program.NativeDllDeclarations.ToLookup(item => item.Declaration.Name,
                StringComparer.OrdinalIgnoreCase);
        private bool truncated;
        private int uncheckedMembers;
        private int uncheckedDynamicCalls;
        private int uncheckedSyntaxNodes;

        internal ScriptCallAnalysis Run()
        {
            foreach (GlobalDeclaration global in program.Globals)
            {
                Visit(global.Initializer, global.Line, null);
                VisitAll(global.Bounds, global.Line, null);
                VisitAll(global.Initializers, global.Line, null);
            }

            VisitStatements(program.Main, null);
            VisitRoutines(program.Routines.Values, null);
            foreach (ModuleDefinition module in program.Modules)
            {
                foreach (NativeDllDeclarationStatement declaration in module.NativeDllDeclarations)
                {
                    CheckNativeDeclaration(declaration);
                }

                foreach (ModuleFieldDefinition field in module.Fields)
                {
                    Visit(field.Initializer, field.Line, module);
                    VisitAll(field.Bounds, field.Line, module);
                    VisitAll(field.Initializers, field.Line, module);
                }

                VisitRoutines(module.Routines.Values, module);
            }

            token.ThrowIfCancellationRequested();
            return new(Array.AsReadOnly(diagnostics
                .OrderBy(item => item.Line).ToArray()), truncated,
                uncheckedMembers, uncheckedDynamicCalls,
                uncheckedSyntaxNodes);
        }

        private void VisitRoutines(IEnumerable<Routine> routines,
            ModuleDefinition? module)
        {
            foreach (Routine routine in routines)
            {
                foreach (Parameter parameter in routine.Parameters)
                {
                    Visit(parameter.Default, routine.Line, module);
                }

                VisitStatements(routine.Body, module);
            }
        }

        private void VisitStatements(IEnumerable<Statement> statements,
            ModuleDefinition? module)
        {
            foreach (Statement statement in statements)
            {
                token.ThrowIfCancellationRequested();
                int line = statement.Line;
                switch (statement)
                {
                    case AssignStatement assignment:
                        Visit(assignment.Expression, line, module);
                        break;
                    case DeclarationGroupStatement declarations:
                        VisitStatements(declarations.Declarations, module);
                        break;
                    case HashDeclareStatement hash:
                        Visit(hash.Initializer, line, module);
                        break;
                    case IndexAssignStatement assignment:
                        Visit(assignment.Target, line, module);
                        Visit(assignment.Expression, line, module);
                        break;
                    case ArrayDeclareStatement array:
                        VisitAll(array.Bounds, line, module);
                        VisitAll(array.Initializers, line, module);
                        break;
                    case PrintStatement print:
                        Visit(print.Expression, line, module);
                        break;
                    case RuntimeOptionsStatement options:
                        foreach (RuntimeOption option in options.Options)
                        {
                            VisitAll(option.Expressions, line, module);
                        }

                        break;
                    case LogOptionsStatement options:
                        foreach (LogOption option in options.Options)
                        {
                            Visit(option.Expression, line, module);
                        }

                        break;
                    case CallStatement call:
                        Visit(call.Call, line, module);
                        break;
                    case ThreadStatement { Call: CallExpr call }:
                        CheckCall(call, line, module, thread: true);
                        VisitAll(call.Arguments, line, module);
                        break;
                    case ThreadStatement thread:
                        Visit(thread.Call, line, module);
                        break;
                    case MemberStatement member:
                        Visit(member.Member, line, module);
                        Visit(member.Assigned, line, module);
                        break;
                    case WithStatement with:
                        Visit(with.Target, line, module);
                        VisitStatements(with.Body, module);
                        break;
                    case IfStatement conditional:
                        foreach (IfBranch branch in conditional.Branches)
                        {
                            Visit(branch.Condition,
                                branch.Line > 0 ? branch.Line : line,
                                module);
                            VisitStatements(branch.Body, module);
                        }

                        break;
                    case ForStatement loop:
                        Visit(loop.Start, line, module);
                        Visit(loop.End, line, module);
                        Visit(loop.Step, line, module);
                        VisitStatements(loop.Body, module);
                        break;
                    case ForInStatement loop:
                        Visit(loop.Collection, line, module);
                        VisitStatements(loop.Body, module);
                        break;
                    case WhileStatement loop:
                        Visit(loop.Condition, line, module);
                        VisitStatements(loop.Body, module);
                        break;
                    case RepeatStatement loop:
                        VisitStatements(loop.Body, module);
                        Visit(loop.Condition, loop.ConditionLine, module);
                        break;
                    case SelectStatement select:
                        Visit(select.Selector, line, module);
                        foreach (CaseBranch branch in select.Cases)
                        {
                            if (branch.Values is not null)
                            {
                                VisitAll(branch.Values, branch.Line, module);
                            }

                            VisitStatements(branch.Body, module);
                        }

                        break;
                    case TryStatement attempt:
                        VisitStatements(attempt.Body, module);
                        VisitStatements(attempt.Handler, module);
                        break;
                    case FlowStatement flow:
                        Visit(flow.ExitCode, line, module);
                        break;
                    case NativeDllDeclarationStatement declaration:
                        if (module is null)
                        {
                            CheckNativeDeclaration(declaration);
                        }

                        break;
                    case ComErrorControlStatement:
                        break;
                    default:
                        uncheckedSyntaxNodes++;
                        break;
                }
            }
        }

        private void VisitAll(IEnumerable<Expr> expressions, int line,
            ModuleDefinition? module)
        {
            foreach (Expr expression in expressions)
            {
                Visit(expression, line, module);
            }
        }

        private void Visit(Expr? expression, int line,
            ModuleDefinition? module)
        {
            token.ThrowIfCancellationRequested();
            switch (expression)
            {
                case null or LiteralExpr or NameExpr or OmittedExpr
                    or WithTargetExpr:
                    break;
                case CallExpr call:
                    CheckCall(call, line, module, thread: false);
                    VisitAll(call.Arguments, line, module);
                    break;
                case MemberExpr member:
                    uncheckedMembers++;
                    Visit(member.Target, line, module);
                    VisitAll(member.Arguments, line, module);
                    break;
                case GroupedExpr grouped:
                    Visit(grouped.Inner, line, module);
                    break;
                case ConditionExpr condition:
                    Visit(condition.Inner, line, module);
                    break;
                case UnaryExpr unary:
                    Visit(unary.Operand, line, module);
                    break;
                case BinaryExpr binary:
                    Visit(binary.Left, line, module);
                    Visit(binary.Right, line, module);
                    break;
                case NamedExpr named:
                    Visit(named.Argument, line, module);
                    break;
                case ReferenceExpr reference:
                    Visit(reference.Target, line, module);
                    break;
                case AssignedMemberExpr assigned:
                    Visit(assigned.Member, line, module);
                    Visit(assigned.Assigned, line, module);
                    break;
                case EvaluatedAssignmentExpr assigned:
                    Visit(assigned.Target, line, module);
                    Visit(assigned.Expression, line, module);
                    break;
                case IndexExpr index:
                    VisitAll(index.Indices, line, module);
                    break;
                case HashCreateExpr hash:
                    Visit(hash.Flags, line, module);
                    break;
                default:
                    uncheckedSyntaxNodes++;
                    break;
            }
        }

        private void CheckCall(CallExpr call, int line,
            ModuleDefinition? module, bool thread)
        {
            if (!thread && BuiltinFunctions.IsBuiltin(call.Name))
            {
                if (StatefulBuiltins.GetKind(call.Name) is
                    StatefulBuiltin.Eval or StatefulBuiltin.SetHotkey or StatefulBuiltin.OleEvent)
                {
                    uncheckedDynamicCalls++;
                }

                try
                {
                    if (!CheckBuiltin(call))
                    {
                        uncheckedSyntaxNodes++;
                        Add(line, "unchecked-call-shape", call.Name,
                            "この組込み関数の引数形式は未確認です。");
                    }
                }
                catch (InvalidOperationException error)
                {
                    Add(line, "argument-shape", call.Name, error.Message);
                }

                return;
            }

            Routine? routine = null;
            if (module is null || !module.Routines.TryGetValue(
                call.Name, out routine))
            {
                if (!thread && module is not null
                    && CheckNativeCall(call, line,
                        module.NativeDllDeclarations.Where(declaration =>
                        declaration.Declaration.Name.Equals(call.Name,
                            StringComparison.OrdinalIgnoreCase))))
                {
                    return;
                }

                program.Routines.TryGetValue(call.Name, out routine);
            }

            if (routine is null)
            {
                if (!thread && CheckNativeCall(call, line, nativeDlls[call.Name]))
                {
                    return;
                }

                Add(line, "unknown-function", call.Name,
                    thread
                        ? "THREADのユーザー関数を静的に解決できません。"
                        : "関数名を組込みまたは同じソースの定義から解決できません。動的な定義は未検査です。");
                return;
            }

            int required = routine.Parameters.Count(
                parameter => parameter.Default is null);
            bool omitted = call.Arguments
                .Take(routine.Parameters.Count)
                .Select((argument, index) => argument is OmittedExpr
                    && routine.Parameters[index].Default is null)
                .Any(value => value);
            if (call.Arguments.Count < required
                || call.Arguments.Count > routine.Parameters.Count
                || omitted
                || call.Arguments.Any(argument => argument is NamedExpr))
            {
                Add(line, "argument-shape", call.Name,
                    "自作関数の引数の数・必須位置・形式が定義と一致しません。");
            }
        }

        private void CheckNativeDeclaration(NativeDllDeclarationStatement statement)
        {
            if (!NativeDllScalarInput.Supports(statement.Declaration))
            {
                AddUnavailableNative(statement.Line, statement.Declaration.Name);
            }
        }

        private bool CheckNativeCall(CallExpr call, int line,
            IEnumerable<NativeDllDeclarationStatement> declarations)
        {
            bool found = false;
            bool supported = false;
            foreach (NativeDllDeclarationStatement statement in declarations)
            {
                found = true;
                if (!NativeDllScalarInput.Supports(statement.Declaration))
                {
                    continue;
                }

                supported = true;
                if (NativeDllScalarInput.MatchesArguments(
                    statement.Declaration, call.Arguments))
                {
                    return true;
                }
            }

            if (supported)
            {
                Add(line, "argument-shape", call.Name,
                    $"{Keywords.DefDll}の引数の数または形式が宣言と一致しません。");
            }
            else if (found)
            {
                AddUnavailableNative(line, call.Name);
            }

            return found;
        }

        private void AddUnavailableNative(int line, string name) =>
            Add(line, "native-dll-unavailable", name,
                $"{Keywords.DefDll}のこの型の組合せには対応していません。");

        private void Add(int line, string code, string function,
            string message)
        {
            if (diagnostics.Count >= DiagnosticLimit)
            {
                truncated = true;
                return;
            }

            diagnostics.Add(new(line, code, function, message));
        }

        private static bool CheckBuiltin(CallExpr call)
        {
            if (StatefulBuiltins.GetKind(call.Name) is StatefulBuiltin.RecoState or StatefulBuiltin.Dictate)
            {
                SpeechRecognitionFlag.CheckCall(call.Name, call.Arguments);
                return true;
            }

            if (StatefulBuiltins.GetKind(call.Name) == StatefulBuiltin.Speak)
            {
                SpeakRequest.CheckCall(call);
                return true;
            }

            if (WindowsFunctions.IsBuiltin(call.Name))
            {
                WindowsFunctions.CheckCall(call.Name, call.Arguments);
                return true;
            }

            if (FileFunctions.IsBuiltin(call.Name))
            {
                (int min, int max) = FileFunctions.GetSignature(call.Name);
                CheckShape(call, min, max, min);
                return true;
            }

            if (BuiltinFunctions.IsPure(call.Name))
            {
                BuiltinFunctions.Signature signature =
                    BuiltinFunctions.GetSignature(call.Name);
                CheckShape(call, signature.Min, signature.Max,
                    signature.Min);
                return true;
            }

            (int minimum, int maximum, int required) =
                StatefulBuiltins.GetKind(call.Name) switch
                {
                    StatefulBuiltin.Sleep => (1, 1, 1),
                    StatefulBuiltin.GetTime => (0, 2, 0),
                    StatefulBuiltin.Random => (0, 1, 0),
                    StatefulBuiltin.Token => (2, 4, 2),
                    StatefulBuiltin.Resize => (1, 2, 1),
                    StatefulBuiltin.SetClear => (1, 2, 1),
                    StatefulBuiltin.ShiftArray => (2, 2, 2),
                    StatefulBuiltin.QSort => (1, 10, 1),
                    StatefulBuiltin.LogPrint => (1, 5, 1),
                    StatefulBuiltin.StopForm => (1, 3, 1),
                    StatefulBuiltin.Eval => (1, 1, 1),
                    StatefulBuiltin.CtrlTrd => (2, 2, 2),
                    StatefulBuiltin.SetHotkey => (1, 3, 0),
                    StatefulBuiltin.Set8087Cw => (0, 1, 0),
                    StatefulBuiltin.OleEvent => (1, 4, 1),
                    _ => (-1, -1, -1)
                };
            if (minimum < 0)
            {
                return false;
            }

            CheckShape(call, minimum, maximum, required);
            if (StatefulBuiltins.GetKind(call.Name) == StatefulBuiltin.OleEvent
                && (call.Arguments.Count is not (1 or 4)
                    || call.Arguments.Any(argument => argument is OmittedExpr)))
            {
                throw new InvalidOperationException(
                    $"{BuiltinFunctionNames.OleEvent}には1引数または4引数を指定してください。");
            }
            if (StatefulBuiltins.GetKind(call.Name)
                == StatefulBuiltin.QSort
                && call.Arguments.Skip(2)
                    .Any(argument => argument is OmittedExpr))
            {
                throw new InvalidOperationException(
                    "QSORTの第3引数以降は省略できません。");
            }

            return true;
        }

        private static void CheckShape(CallExpr call, int minimum,
            int maximum, int required)
        {
            if (call.Arguments.Count < minimum
                || call.Arguments.Count > maximum
                || call.Arguments.Take(required)
                    .Any(argument => argument is OmittedExpr))
            {
                throw new InvalidOperationException(
                    $"引数の数または省略位置が不正です: {call.Name}");
            }
        }
    }
}
