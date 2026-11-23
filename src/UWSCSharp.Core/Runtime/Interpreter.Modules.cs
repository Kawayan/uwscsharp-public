namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed class ModuleState(ModuleDefinition definition)
    {
        internal ModuleDefinition Definition
        {
            get;
        } = definition;

        internal NativeDllRegistry NativeDlls
        {
            get;
        } = new(definition.NativeDllDeclarations);

        internal System.Collections.Concurrent.ConcurrentDictionary<string, Value> Fields
        {
            get;
        } = new(
            StringComparer.OrdinalIgnoreCase);

        internal ModuleFieldDefinition? FindField(string name) =>
            Definition.Fields.FirstOrDefault(field => field.Name.Equals(
                name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CoreNamespace(ModuleState? Module);

    private sealed partial class Execution
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ModuleState> modules = new(
            StringComparer.OrdinalIgnoreCase);

        private bool TryModuleField(Scope caller, string name,
            out ModuleState state, out ModuleFieldDefinition field)
        {
            int dot = name.IndexOf('.');
            ModuleState? owner = caller.Module;
            string member = name;
            if (dot >= 0)
            {
                Value target = caller.Read(name[..dot]);
                if (target.ObjectReference is not CoreNamespace space
                    || space.Module is null)
                {
                    throw new InvalidOperationException(
                        "この名前空間では変数を参照できません。");
                }

                owner = space.Module;
                member = name[(dot + 1)..];
            }

            ModuleFieldDefinition? definition = owner?.FindField(member);
            if (definition is not null)
            {
                if (definition.Kind == ModuleFieldKind.Private
                    && !ReferenceEquals(owner, caller.Module))
                {
                    throw new InvalidOperationException(
                        $"非公開のモジュール変数です: {owner!.Definition.Name}.{member}");
                }

                state = owner!;
                field = definition;
                return true;
            }

            if (dot >= 0)
            {
                throw new InvalidOperationException($"未定義のモジュール変数: {name}");
            }

            state = null!;
            field = null!;
            return false;
        }

        internal bool TryReadModule(Scope caller, string name, out Value value)
        {
            if (TryModuleField(caller, name, out ModuleState state,
                out ModuleFieldDefinition field))
            {
                if (!state.Fields.TryGetValue(field.Name, out value))
                {
                    throw new InvalidOperationException(
                        $"未初期化のモジュール変数: {state.Definition.Name}.{field.Name}");
                }

                return true;
            }

            if (name.Equals(Keywords.This, StringComparison.OrdinalIgnoreCase))
            {
                if (caller.Module is null)
                {
                    throw new InvalidOperationException("THISはモジュール内で使用してください。");
                }

                value = Value.FromObject(new CoreNamespace(caller.Module));
                return true;
            }

            if (name.Equals(Keywords.Global, StringComparison.OrdinalIgnoreCase))
            {
                value = Value.FromObject(new CoreNamespace(null));
                return true;
            }

            if (TryGetModule(name, out ModuleState? module))
            {
                value = Value.FromObject(new CoreNamespace(module));
                return true;
            }

            value = default;
            return false;
        }

        internal bool TryWriteModule(Scope caller, string name, Value value)
        {
            if (!TryModuleField(caller, name, out ModuleState state,
                out ModuleFieldDefinition field))
            {
                return false;
            }

            WriteModuleField(state, field, value);
            return true;
        }

        private static void WriteModuleField(ModuleState state,
            ModuleFieldDefinition field, Value value)
        {
            if (field.Kind == ModuleFieldKind.Constant)
            {
                throw new InvalidOperationException($"定数は変更できません: {field.Name}");
            }

            if (state.Fields.TryGetValue(field.Name, out Value previous)
                && previous.Kind == ValueKind.HashTable)
            {
                if (value.Kind == ValueKind.Number && value.Number == HashConstants.RemoveAll)
                {
                    value = Value.FromHashTable(previous.AsHashTable().Clear());
                }
                else if (value.Kind != ValueKind.HashTable)
                {
                    throw new InvalidOperationException(
                        "HASHTBLへの代入にはHASHTBLまたはHASH_REMOVEALLを指定してください。");
                }
            }

            state.Fields[field.Name] = value;
        }

        private bool ModuleConstant(Scope caller, string name) =>
            TryModuleField(caller, name, out _, out ModuleFieldDefinition field)
                ? field.Kind == ModuleFieldKind.Constant : constants.Contains(name);

        private static Expr ResolveWithReference(Expr reference, Scope? caller)
        {
            if (reference is MemberExpr { Target: WithTargetExpr } member
                && caller is not null)
            {
                return member with
                {
                    Target = new NameExpr(caller.BindWithModuleReference())
                };
            }

            return reference;
        }

        private static Expr NormalizeModuleReference(Expr reference, Scope caller) =>
            ResolveWithReference(reference, caller) switch
            {
                ReferenceExpr wrapper => NormalizeModuleReference(wrapper.Target, caller),
                MemberExpr { Target: NameExpr owner, Indexed: true } member =>
                    new IndexExpr(owner.Name + "." + member.Name, member.Arguments),
                MemberExpr { Target: NameExpr owner, Arguments.Count: 0 } member =>
                    new NameExpr(owner.Name + "." + member.Name),
                _ => reference
            };

        internal async ValueTask<Value> InvokeCoreMemberAsync(Value target,
            MemberExpr member, Expr? assignment, Scope caller)
        {
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            var space = (CoreNamespace)target.ObjectReference!;
            ModuleState? module = space.Module;
            if (module is null)
            {
                if (assignment is not null || member.Indexed)
                {
                    throw new InvalidOperationException(
                        "GLOBALにはモジュール外の関数を指定してください。");
                }

                if (TryGetGlobalRoutine(member.Name, out Routine? global))
                {
                    return await InvokeRoutineDefinitionAsync(
                        new CallExpr(member.Name, member.Arguments), caller,
                        global, null).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }

                // 実引数は呼出し元のモジュールとローカルを使って評価します。
                if (!BuiltinFunctions.IsBuiltin(member.Name)
                    && !FileFunctions.IsBuiltin(member.Name)
                    && !WindowsFunctions.IsBuiltin(member.Name))
                {
                    if (ActiveNativeDlls.TryGet(member.Name,
                        out NativeDllDeclarationStatement? declaration))
                    {
                        return await InvokeNativeDllAsync(declaration,
                            member.Arguments, caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }

                    throw new InvalidOperationException($"未定義の関数: {member.Name}");
                }

                return await InvokeAsync(new CallExpr(member.Name,
                    member.Arguments), caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            ModuleFieldDefinition? field = module.FindField(member.Name);
            if (field is not null)
            {
                if (field.Kind == ModuleFieldKind.Private
                    && !ReferenceEquals(module, caller.Module))
                {
                    throw new InvalidOperationException(
                        $"非公開のモジュール変数です: {module.Definition.Name}.{field.Name}");
                }

                if (!member.Indexed && member.Arguments.Count > 0)
                {
                    throw new InvalidOperationException("変数に関数引数は指定できません。");
                }

                if (!module.Fields.TryGetValue(field.Name, out Value value))
                {
                    throw new InvalidOperationException($"未初期化のモジュール変数: {field.Name}");
                }

                if (assignment is not null && field.Kind == ModuleFieldKind.Constant)
                {
                    throw new InvalidOperationException($"定数は変更できません: {field.Name}");
                }

                int[] indices = member.Indexed
                    ? await new IndexExpr(field.Name, member.Arguments)
                        .ResolveAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent) : [];
                if (assignment is null)
                {
                    return member.Indexed ? value.AsArray().Get(indices) : value;
                }

                Value replacement = await assignment.EvaluateAsync(caller,
                    token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                lock (sharedVariablesGate)
                {
                    if (member.Indexed)
                    {
                        // 右辺の副作用と他workerの最新の配列内容を保持します。
                        Value current = module.Fields[field.Name];
                        replacement = current with
                        {
                            Array = current.AsArray().Set(indices, replacement, token)
                        };
                    }
                    WriteModuleField(module, field, replacement);
                }
                return replacement;
            }

            if (assignment is null && !member.Indexed)
            {
                if (module.Definition.Routines.TryGetValue(member.Name, out Routine? routine))
                {
                    return await InvokeRoutineDefinitionAsync(new CallExpr(member.Name,
                        member.Arguments), caller, routine, module).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }

                if (module.NativeDlls.TryGet(member.Name,
                    out NativeDllDeclarationStatement? declaration))
                {
                    return await InvokeNativeDllAsync(declaration,
                        member.Arguments, caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
            }

            throw new InvalidOperationException($"未定義のモジュールメンバー: {member.Name}");
        }

        private async ValueTask InitializeDeclarationsAsync(Scope main,
            Func<GlobalDeclaration, ValueTask> initializeGlobal)
        {
            foreach (ModuleDefinition definition in program.Modules)
            {
                modules.TryAdd(definition.Name, new ModuleState(definition));
            }

            var files = program.InitializationFiles.Select((path, index) =>
                (path, index)).ToDictionary(item => item.path,
                    item => item.index, StringComparer.OrdinalIgnoreCase);
            var declarations = new List<(string? Path, int Order,
                GlobalDeclaration? Global, ModuleState? Module,
                ModuleFieldDefinition? Field)>();
            foreach (GlobalDeclaration global in program.Globals)
            {
                declarations.Add((global.SourcePath,
                    global.SourceOrder == int.MaxValue ? global.Line : global.SourceOrder,
                    global, null, null));
            }

            foreach (ModuleState module in program.Modules.Select(
                definition => modules[definition.Name]))
            {
                foreach (ModuleFieldDefinition field in module.Definition.Fields)
                {
                    declarations.Add((field.SourcePath,
                        field.SourceOrder == int.MaxValue ? field.Line : field.SourceOrder,
                        null, module, field));
                }
            }

            foreach ((string? Path, int Order, GlobalDeclaration? Global, ModuleState? Module, ModuleFieldDefinition? Field) declaration in declarations.OrderBy(item =>
                item.Path is not null && files.TryGetValue(item.Path, out int index)
                    ? index : 0).ThenBy(item => item.Order))
            {
                if (declaration.Global is not null)
                {
                    await initializeGlobal(declaration.Global).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    continue;
                }

                ModuleState module = declaration.Module!;
                ModuleFieldDefinition field = declaration.Field!;
                var scope = new Scope(this) { Module = module, SourcePath = field.SourcePath, CurrentLine = field.Line };
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

                    if (field.Kind == ModuleFieldKind.Constant
                        && value.Kind is ValueKind.Array or ValueKind.HashTable or ValueKind.Object)
                    {
                        throw new ScriptException(field.Line,
                            "CONSTの初期値には単一の値を指定してください。");
                    }

                    module.Fields[field.Name] = value;
                }
                catch (ScriptException error)
                {
                    error.SetSourcePath(field.SourcePath);
                    throw;
                }
            }

            foreach (ModuleState module in program.Modules.Select(
                definition => modules[definition.Name]))
            {
                if (module.Definition.Routines.TryGetValue(module.Definition.Name,
                    out Routine? constructor))
                {
                    try
                    {
                        await InvokeRoutineDefinitionAsync(new CallExpr(constructor.Name,
                            []), main, constructor, module).ConfigureAwait(WorkerThreadContext.IsCurrent);
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
