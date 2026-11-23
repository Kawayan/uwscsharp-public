using System.Runtime.ExceptionServices;

namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private async ValueTask<Value> ReadNativeDllArrayArgumentAsync(
            Expr argument, Scope caller)
        {
            Expr target = NormalizeModuleReference(argument, caller);
            (string? name, bool publicStorage) = NativeDllArrayVariable(target, caller);
            if (name is null)
            {
                return await EvaluateNativeDllArgumentAsync(argument, caller)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            if (!publicStorage && target is IndexExpr
                && caller.Read(name).Kind != ValueKind.Array)
            {
                return await EvaluateNativeDllArgumentAsync(argument, caller)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            int[] indices = [];
            if (target is IndexExpr index)
            {
                indices = new int[index.Indices.Count];
                for (int i = 0; i < indices.Length; i++)
                {
                    Value coordinate = await EvaluateNativeDllArgumentAsync(
                        index.Indices[i], caller)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    indices[i] = ArrayOperations.Coordinate(coordinate);
                }
            }
            lock (sharedVariablesGate)
            {
                Value declared = caller.Read(name);
                Value requested = declared;
                int offset = 0;
                if (declared.Kind == ValueKind.Array)
                {
                    UwscArray array = NativeDllArray(declared);
                    int[] canonical = NativeDllArrayIndices(array, indices, out offset);
                    requested = target is IndexExpr ? array.Get(canonical) : declared;
                }
                else if (target is IndexExpr)
                {
                    throw new InvalidOperationException(
                        "DLLの配列要素には配列変数を指定してください。");
                }

                if (!publicStorage)
                {
                    return requested;
                }

                // 原版はPUBLICの形状を使い、同じ登録位置のローカル値を読みます。
                int ordinal = 0;
                foreach (string publicName in publicDeclarations.Keys)
                {
                    if (constants.Contains(publicName))
                    {
                        continue;
                    }
                    if (publicName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    ordinal++;
                }

                Value local = NativeDllLocalValue(caller, ordinal);
                IReadOnlyList<Value> cells = local.Kind == ValueKind.Array
                    ? local.AsArray().Values : [local];
                UwscArray? requestedArray = requested.Kind == ValueKind.Array
                    ? requested.AsArray() : null;
                int count = requestedArray?.Count ?? 1;
                if (count == 0 || offset < 0 || offset > cells.Count - count)
                {
                    throw new InvalidOperationException(
                        "DLLのPUBLIC配列引数に対応するローカル値が不足しています。");
                }

                return requestedArray is null ? cells[offset]
                    : Value.FromArray(requestedArray.WithValues(
                        cells.Skip(offset).Take(count), token));
            }
        }

        private (string? Name, bool PublicStorage) NativeDllArrayVariable(
            Expr target, Scope caller)
        {
            string? name = target switch
            {
                NameExpr variable => variable.Name,
                IndexExpr indexed => indexed.Name,
                _ => null
            };
            if (name is null)
            {
                return (null, false);
            }
            if (caller.Locals.ContainsKey(name))
            {
                return (name, false);
            }
            if (TryReadThreadVariable(name, out _)
                || TryReadModule(caller, name, out _)
                || !publicDeclarations.ContainsKey(name))
            {
                return (null, false);
            }

            return (name, true);
        }

        private static int[] NativeDllArrayIndices(UwscArray array,
            IReadOnlyList<int> indices, out int offset)
        {
            if (!TryNativeDllArrayIndices(array, indices, out offset,
                out int[] canonical))
            {
                throw new InvalidOperationException(
                    "DLLの配列引数の添字が範囲外です。");
            }
            return canonical;
        }

        private static bool TryNativeDllArrayIndices(UwscArray array,
            IReadOnlyList<int> indices, out int offset, out int[] canonical)
        {
            offset = 0;
            canonical = [];
            if (indices.Count > array.Rank)
            {
                throw new InvalidOperationException(
                    "DLLの配列引数の添字の数が不正です。");
            }

            // DLL配列引数では次元ごとではなく、通し位置と残りの個数を検査します。
            long linear = 0;
            int stride = array.Count;
            for (int dimension = 0; dimension < indices.Count; dimension++)
            {
                stride /= array.GetLength(dimension);
                linear += ((long)indices[dimension]
                    - array.LowerBounds[dimension]) * stride;
            }
            if (linear < 0 || linear > array.Count - stride)
            {
                return false;
            }

            offset = (int)linear;
            int remainder = offset;
            stride = array.Count;
            canonical = new int[indices.Count];
            for (int dimension = 0; dimension < canonical.Length; dimension++)
            {
                stride /= array.GetLength(dimension);
                canonical[dimension] = remainder / stride
                    + array.LowerBounds[dimension];
                remainder %= stride;
            }

            return true;
        }

        private async ValueTask<Value[]> ResolveNativeDllWriteBackIndicesAsync(
            IndexExpr index, Scope caller, bool numeric)
        {
            var indices = new Value[index.Indices.Count];
            for (int i = 0; i < indices.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    Value coordinate = await index.Indices[i].EvaluateAsync(caller, token)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    indices[i] = numeric
                        ? Value.Numeric(ArrayOperations.Coordinate(coordinate)) : coordinate;
                }
                catch (NativeDllIndexAbortSignal abort) when (
                    ReferenceEquals(abort.Context, State.NativeDllWriteBack))
                {
                    // 失敗した添字は0にし、同じ配列の次の次元も評価します。
                    indices[i] = Value.Numeric(0);
                }
                catch (Exception error) when (finallyDepth == 0
                    && State.LifetimeToken.IsCancellationRequested
                    && (error is ScriptException || ScriptErrors.IsScriptError(error)))
                {
                    // 停止後の空文字列を数値化できない場合も、残るDLL引数を反映します。
                    State.NativeDllWriteBack!.CopybackFailure ??=
                        ExceptionDispatchInfo.Capture(error);
                    indices[i] = Value.Numeric(0);
                }
            }
            return indices;
        }

        private void WriteNativeDllArray(
            string name, Value[] coordinates, Value value, Scope caller)
        {
            int[] indices = [.. coordinates.Select(ArrayOperations.Coordinate)];
            lock (sharedVariablesGate)
            {
                Value current = caller.Read(name);
                UwscArray array = NativeDllArray(current);
                if (!TryNativeDllArrayIndices(array, indices, out _,
                    out int[] canonical))
                {
                    // 原版に合わせ、範囲外となった配列の書戻しだけを飛ばします。
                    return;
                }
                caller.Write(name, current with
                {
                    Array = array.Set(canonical, value, token)
                });
            }
        }

        private Value NativeDllLocalValue(Scope caller, int ordinal)
        {
            if (!caller.HasRoutineResult)
            {
                // Mainの先頭位置はPARAM_STRです。
                if (ordinal == 0)
                {
                    return State.ParameterArguments;
                }
                ordinal--;
            }

            foreach ((string name, Value value) in caller.Locals)
            {
                if (name.StartsWith('\0')
                    || (caller.HasRoutineResult && !caller.RoutineReturnsValue
                        && name.Equals(BuiltinVariableNames.Result,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (ordinal-- == 0)
                {
                    return value;
                }
            }

            throw new InvalidOperationException(
                "DLLのPUBLIC配列引数に対応するローカル変数がありません。");
        }
    }
}
