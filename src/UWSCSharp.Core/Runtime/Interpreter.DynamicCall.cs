using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private sealed class DynamicImport(NativeDllRegistry nativeDlls)
        {
            internal volatile bool Active = true;
            internal NativeDllRegistry NativeDlls { get; } = nativeDlls;
            internal readonly Dictionary<string, Routine> Routines = new(
                StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, ModuleState> Modules = new(
                StringComparer.OrdinalIgnoreCase);
            internal bool SameString;
            internal bool ShortCircuit;
            internal bool ExplicitVariables;
            internal bool OptFinally;
            internal bool OptPublic;
        }

        private readonly ConcurrentDictionary<string, Routine> runtimeRoutines = new(
            program.Routines, StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim dynamicCallGate = new(1, 1);
        private readonly AsyncLocal<DynamicImport?> dynamicImport = new();
        private readonly Dictionary<string, ExceptionDispatchInfo> dynamicFailures = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private ScriptCompilation? dynamicCompilation;
        private volatile bool dynamicSameString;
        private volatile bool dynamicShortCircuit;
        private volatile bool dynamicExplicitVariables;
        private volatile bool dynamicOptFinally;
        private volatile bool dynamicOptPublic;

        private DynamicImport? ActiveDynamicImport =>
            dynamicImport.Value is { Active: true } import ? import : null;

        private NativeDllRegistry ActiveNativeDlls =>
            dynamicImport.Value?.NativeDlls ?? nativeDlls;

        internal bool EffectiveShortCircuit => program.ShortCircuit
            || dynamicShortCircuit || ActiveDynamicImport?.ShortCircuit == true;

        internal bool EffectiveSameString => program.SameString
            || dynamicSameString || ActiveDynamicImport?.SameString == true;

        internal bool EffectiveExplicitVariables => program.ExplicitVariables
            || dynamicExplicitVariables || ActiveDynamicImport?.ExplicitVariables == true;

        internal bool EffectiveOptFinally => program.OptFinally
            || dynamicOptFinally || ActiveDynamicImport?.OptFinally == true;

        internal bool EffectiveOptPublic => program.OptPublic
            || dynamicOptPublic || ActiveDynamicImport?.OptPublic == true;

        private bool TryGetGlobalRoutine(string name,
            [NotNullWhen(true)] out Routine? routine)
        {
            if (ActiveDynamicImport is { } import
                && import.Routines.TryGetValue(name, out routine))
            {
                return true;
            }

            return runtimeRoutines.TryGetValue(name, out routine);
        }

        private bool TryGetModule(string name,
            [NotNullWhen(true)] out ModuleState? module)
        {
            if (ActiveDynamicImport is { } import
                && import.Modules.TryGetValue(name, out module))
            {
                return true;
            }

            return modules.TryGetValue(name, out module);
        }

        private async ValueTask<Value> InvokeDynamicCallAsync(string text,
            CallExpr evalCall, Scope caller)
        {
            if (ActiveDynamicImport is not null)
            {
                throw new InvalidOperationException(
                    "動的CALLの初期化中にEVALのCALLを再入実行できません。");
            }

            if (caller.CallDepth >= ScriptLimits.MaxCallDepth)
            {
                throw new InvalidOperationException("CALLの呼出し階層が深すぎます。");
            }

            ExternalCallStatement external;
            await dynamicCallGate.WaitAsync(token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            try
            {
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                dynamicCompilation ??= ScriptCompilation.CreateRuntime(program, token);
                ScriptCompilation staged = dynamicCompilation.Fork(token);
                string parentPath = caller.SourcePath ?? dynamicCompilation.BasePath;
                try
                {
                    external = ScriptCompiler.ParseDynamicCall(text, staged,
                        parentPath, evalCall.PreserveSpecialCharacters, token) with
                    {
                        Line = caller.CurrentLine > 0 ? caller.CurrentLine : 1
                    };
                    staged.ParsePending();
                }
                catch (ScriptException error) when (!error.Data.Contains("UWSCSharp.SourcePath")
                    || string.Equals(error.Data["UWSCSharp.SourcePath"] as string,
                        parentPath, OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    // CALL文字列自体の構文エラーは、外側のEVAL行へ接続します。
                    string prefix = $"{error.Line}行: ";
                    string detail = error.Message.StartsWith(prefix, StringComparison.Ordinal)
                        ? error.Message[prefix.Length..] : error.Message;
                    throw new InvalidOperationException(detail, error);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    var failure = new ScriptException(
                        caller.CurrentLine > 0 ? caller.CurrentLine : 1,
                        error.Message, error);
                    failure.SetSourcePath(error.Data["UWSCSharp.SourcePath"] as string ?? parentPath);
                    throw failure;
                }

                IReadOnlyList<ExternalScript> oldFiles = dynamicCompilation.Snapshot();
                var oldPaths = oldFiles.Select(script => script.Path).ToHashSet(
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                ExternalScript[] added = [.. staged.Snapshot().Where(script =>
                    !oldPaths.Contains(script.Path))];
                if (dynamicFailures.TryGetValue(external.Script.Path, out ExceptionDispatchInfo? failed))
                {
                    failed.Throw();
                }

                foreach (ExternalScript script in added)
                {
                    if (dynamicFailures.TryGetValue(script.Path, out failed))
                    {
                        failed.Throw();
                    }
                }

                if (added.Length > 0)
                {
                    DynamicImport import = PrepareDynamicImport(added);
                    dynamicImport.Value = import;
                    try
                    {
                        await InitializeDynamicImportAsync(added, import,
                            caller.CallDepth).ConfigureAwait(WorkerThreadContext.IsCurrent);
                        import.NativeDlls.Publish();
                        foreach (KeyValuePair<string, Routine> routine in import.Routines)
                        {
                            runtimeRoutines.TryAdd(routine.Key, routine.Value);
                        }

                        foreach (KeyValuePair<string, ModuleState> module in import.Modules)
                        {
                            modules.TryAdd(module.Key, module.Value);
                        }

                        dynamicSameString |= import.SameString;
                        dynamicShortCircuit |= import.ShortCircuit;
                        dynamicExplicitVariables |= import.ExplicitVariables;
                        dynamicOptFinally |= import.OptFinally;
                        dynamicOptPublic |= import.OptPublic;
                        dynamicCompilation = staged;
                    }
                    catch (Exception error)
                    {
                        // 完了済みのPUBLIC更新や外部副作用は巻き戻しません。
                        // 同じrunでの再初期化による重複した副作用を防ぎます。
                        foreach (ExternalScript script in added)
                        {
                            dynamicFailures[script.Path] = ExceptionDispatchInfo.Capture(error);
                        }

                        throw;
                    }
                    finally
                    {
                        import.Active = false;
                        dynamicImport.Value = null;
                    }
                }
            }
            finally
            {
                dynamicCallGate.Release();
            }

            // CALL本体はgateの外で動かし、通常の入れ子CALLを許可します。
            await CallExternalAsync(external, caller)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            return Value.String("");
        }

        private DynamicImport PrepareDynamicImport(IReadOnlyList<ExternalScript> files)
        {
            var import = new DynamicImport(new NativeDllRegistry(
                files.SelectMany(script => script.Program.NativeDllDeclarations),
                nativeDlls))
            {
                SameString = files.Any(script => script.Program.SameString),
                ShortCircuit = files.Any(script => script.Program.ShortCircuit),
                ExplicitVariables = files.Any(script => script.Program.ExplicitVariables),
                OptFinally = files.Any(script => script.Program.OptFinally),
                OptPublic = files.Any(script => script.Program.OptPublic)
            };
            var declarations = publicDeclarations.ToDictionary(pair => pair.Key,
                pair => (pair.Value.Kind, Constant: constants.Contains(pair.Key)),
                StringComparer.OrdinalIgnoreCase);
            foreach (ExternalScript script in files)
            {
                foreach (Routine routine in script.Program.Routines.Values)
                {
                    if (runtimeRoutines.ContainsKey(routine.Name)
                        || !import.Routines.TryAdd(routine.Name, routine))
                    {
                        throw DynamicDefinitionError(routine.Line,
                            $"CALL間で関数名が重複しています: {routine.Name}", script.Path);
                    }
                }

                foreach (ModuleDefinition module in script.Program.Modules)
                {
                    if (modules.ContainsKey(module.Name)
                        || !import.Modules.TryAdd(module.Name, new ModuleState(module)))
                    {
                        throw DynamicDefinitionError(module.Line,
                            $"CALL間でモジュール名が重複しています: {module.Name}", script.Path);
                    }
                }

                foreach (GlobalDeclaration declaration in script.Program.Globals)
                {
                    if (declarations.TryGetValue(declaration.Name, out (GlobalDeclarationKind Kind, bool Constant) previous)
                        && (EffectiveOptPublic || import.OptPublic || previous.Constant
                            || declaration.Constant || previous.Kind != declaration.Kind
                            || declaration.Kind == GlobalDeclarationKind.HashTable))
                    {
                        throw DynamicDefinitionError(declaration.Line,
                            $"CALL間でグローバル名が重複しています: {declaration.Name}", script.Path);
                    }

                    declarations.TryAdd(declaration.Name,
                        (declaration.Kind, declaration.Constant));
                }
            }

            return import;
        }

        private static ScriptException DynamicDefinitionError(int line,
            string message, string path)
        {
            var error = new ScriptException(line, message);
            error.SetSourcePath(path);
            return error;
        }

        private async ValueTask InitializeDynamicImportAsync(
            IReadOnlyList<ExternalScript> files, DynamicImport import, int callDepth)
        {
            foreach (ExternalScript script in files)
            {
                var declarations = new List<(int Order,
                    GlobalDeclaration? Global, ModuleState? Module,
                    ModuleFieldDefinition? Field)>();
                foreach (GlobalDeclaration declaration in script.Program.Globals)
                {
                    declarations.Add((declaration.SourceOrder, declaration, null, null));
                }

                foreach (ModuleDefinition definition in script.Program.Modules)
                {
                    ModuleState module = import.Modules[definition.Name];
                    foreach (ModuleFieldDefinition field in definition.Fields)
                    {
                        declarations.Add((field.SourceOrder, null, module, field));
                    }
                }

                foreach ((int Order, GlobalDeclaration? Global, ModuleState? Module, ModuleFieldDefinition? Field) declaration in declarations.OrderBy(item => item.Order))
                {
                    if (declaration.Global is { } global)
                    {
                        await InitializeGlobalDeclarationAsync(global, new Scope(this)
                        {
                            CallDepth = callDepth,
                            SourcePath = global.SourcePath,
                            CurrentLine = global.Line
                        }).ConfigureAwait(WorkerThreadContext.IsCurrent);
                        continue;
                    }

                    ModuleFieldDefinition field = declaration.Field!;
                    ModuleState module = declaration.Module!;
                    var scope = new Scope(this)
                    {
                        Module = module,
                        CallDepth = callDepth,
                        SourcePath = field.SourcePath,
                        CurrentLine = field.Line
                    };
                    try
                    {
                        Value value;
                        if (field.Bounds.Count > 0 || field.Infer)
                        {
                            await DeclareArrayAsync(new ArrayDeclareStatement(field.Line,
                                field.Name, field.Bounds, field.Initializers,
                                field.Infer), scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                            value = scope.Locals[field.Name];
                        }
                        else
                        {
                            value = field.Initializer is null ? Value.Empty
                                : await EvaluateAsync(field.Initializer, scope,
                                    field.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                        }

                        if (field.Kind == ModuleFieldKind.Constant && value.Kind is
                            ValueKind.Array or ValueKind.HashTable or ValueKind.Object)
                        {
                            throw new ScriptException(field.Line,
                                "CONSTの初期値には単一の値を指定してください。");
                        }

                        module.Fields.TryAdd(field.Name, value);
                    }
                    catch (ScriptException error)
                    {
                        error.SetSourcePath(field.SourcePath);
                        throw;
                    }
                }
            }

            foreach (ModuleState module in import.Modules.Values)
            {
                if (module.Definition.Routines.TryGetValue(module.Definition.Name,
                    out Routine? constructor))
                {
                    try
                    {
                        await InvokeRoutineDefinitionAsync(new CallExpr(constructor.Name, []),
                            new Scope(this)
                            {
                                CallDepth = callDepth,
                                SourcePath = module.Definition.SourcePath,
                                CurrentLine = constructor.Line
                            }, constructor, module).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }
                    catch (ScriptException error)
                    {
                        error.SetSourcePath(module.Definition.SourcePath);
                        throw;
                    }
                }
            }
        }
    }
}
