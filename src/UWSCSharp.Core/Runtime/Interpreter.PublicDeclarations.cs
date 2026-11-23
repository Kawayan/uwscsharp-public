namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private sealed record PublicDeclarationShape(
            GlobalDeclarationKind Kind, IReadOnlyList<int> Bounds);

        // 宣言の形状は格納値の型とは別に保持します。
        // スカラーに配列値が入っても、配列宣言へ変換したとは扱いません。
        private readonly Dictionary<string, PublicDeclarationShape> publicDeclarations = new(
            StringComparer.OrdinalIgnoreCase);

        private async ValueTask InitializeGlobalDeclarationAsync(
            GlobalDeclaration declaration, Scope scope)
        {
            scope = new Scope(this)
            {
                CallDepth = scope.CallDepth,
                SourcePath = declaration.SourcePath ?? scope.SourcePath,
                CurrentLine = declaration.Line
            };

            try
            {
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                if (declaration.Name.Equals(BuiltinVariableNames.ParamStr,
                    StringComparison.OrdinalIgnoreCase)
                    || BuiltinVariableNames.IsExecutionInformation(
                        declaration.Name))
                {
                    throw new InvalidOperationException(
                        $"{declaration.Name}をPUBLICまたはCONSTで再定義できません。");
                }

                if (BuiltinConstants.TryGet(declaration.Name, out _))
                {
                    throw new InvalidOperationException(
                        $"組み込み定数を再定義できません: {declaration.Name}");
                }

                bool exists = publicDeclarations.TryGetValue(declaration.Name,
                    out PublicDeclarationShape? previous);
                if (exists && (EffectiveOptPublic || declaration.Constant
                    || constants.Contains(declaration.Name)
                    || previous!.Kind != declaration.Kind
                    || declaration.Kind == GlobalDeclarationKind.HashTable))
                {
                    throw new InvalidOperationException(
                        $"宣言が重複しています: {declaration.Name}");
                }

                var bounds = new List<int>();
                foreach (Expr bound in declaration.Bounds)
                {
                    Value result = await EvaluateAsync(bound, scope,
                        declaration.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    bounds.Add(At(declaration.Line,
                        () => ArrayOperations.Coordinate(result)));
                }

                if (declaration.Infer)
                {
                    bounds.Add(declaration.Initializers.Count - 1);
                }

                if (exists && !previous!.Bounds.SequenceEqual(bounds))
                {
                    throw new InvalidOperationException(
                        $"再宣言の配列形状が異なります: {declaration.Name}");
                }

                Value value;
                if (declaration.Kind == GlobalDeclarationKind.Array)
                {
                    // 形状だけの再宣言では、要素を初期化し直しません。
                    if (exists && declaration.Initializers.Count == 0)
                    {
                        return;
                    }

                    var values = new List<Value>();
                    foreach (Expr initializer in declaration.Initializers)
                    {
                        values.Add(ArrayElement(await EvaluateAsync(initializer,
                            scope, declaration.Line).ConfigureAwait(WorkerThreadContext.IsCurrent), declaration.Line));
                    }

                    value = At(declaration.Line,
                        () => Value.FromArray(UwscArray.Create(bounds, values)));
                }
                else
                {
                    if (exists && declaration.Initializer is null)
                    {
                        return;
                    }

                    value = declaration.Initializer is null ? Value.Empty
                        : await EvaluateAsync(declaration.Initializer, scope,
                            declaration.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }

                if (declaration.Constant && value.Kind is
                    ValueKind.Array or ValueKind.HashTable or ValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "CONSTの初期値には単一の値を指定してください。");
                }

                globals[declaration.Name] = value;
                if (!exists)
                {
                    publicDeclarations.Add(declaration.Name,
                        new(declaration.Kind, bounds.AsReadOnly()));
                }

                if (declaration.Constant)
                {
                    constants.Add(declaration.Name);
                }
            }
            catch (ScriptException error)
            {
                error.SetSourcePath(declaration.SourcePath);
                throw;
            }
            catch (Exception error) when (ScriptErrors.IsScriptError(error))
            {
                var failure = new ScriptException(declaration.Line, error.Message, error);
                failure.SetSourcePath(declaration.SourcePath);
                throw failure;
            }
        }
    }
}
