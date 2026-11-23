using System.Runtime.ExceptionServices;
using System.Text;

namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private async ValueTask<Flow> CallExternalAsync(
            ExternalCallStatement external, Scope scope)
        {
            try
            {
                return await RunExternalCallAsync(external, scope)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (ScriptException error) when (finallyDepth == 0
                && !State.LifetimeToken.IsCancellationRequested
                && State.NativeDllRoutine is { } frame
                && frame.WriteBackDepth == State.NativeDllWriteBackDepth)
            {
                // 添字関数で未処理なら添字0へ書き戻し、最初のエラーを通知します。
                frame.CopybackFailure = error;
                throw;
            }
        }

        private async ValueTask<Flow> RunExternalCallAsync(
            ExternalCallStatement external, Scope scope)
        {
            if (scope.CallDepth >= ScriptLimits.MaxCallDepth)
            {
                throw new ScriptException(external.Line, "CALLの呼出し階層が深すぎます。");
            }

            var values = new List<Value>();
            foreach (Expr argument in external.Arguments)
            {
                Value callArgument = await EvaluateAsync(argument, scope, external.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                values.Add(At(external.Line,
                    () => Value.String(callArgument.Display())));
            }

            Value previousArguments = State.ParameterArguments;
            State.ParameterArguments = Value.FromArray(UwscArray.Create([values.Count - 1], values));
            try
            {
                await Task.CompletedTask.ConfigureAwait((WorkerThreadContext.IsCurrent ? ConfigureAwaitOptions.ContinueOnCapturedContext : ConfigureAwaitOptions.None) | ConfigureAwaitOptions.ForceYielding);
                await BlockAsync(external.Script.Program.Main,
                    new Scope(this) { CallDepth = scope.CallDepth + 1, SourcePath = external.Script.Path }).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception error)
            {
                if (!error.Data.Contains("UWSCSharp.SourcePath"))
                {
                    error.Data["UWSCSharp.SourcePath"] = external.Script.Path;
                }

                throw;
            }
            finally
            {
                State.ParameterArguments = previousArguments;
            }

            return default;
        }

        private async ValueTask<Flow> SelectAsync(
            SelectStatement select, Scope scope)
        {
            Value selector = await EvaluateAsync(
                select.Selector, scope, select.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
            foreach (CaseBranch branch in select.Cases)
            {
                bool matches = branch.Values is null;
                if (branch.Values is not null)
                {
                    foreach (Expr expression in branch.Values)
                    {
                        Value candidate = await EvaluateAsync(
                            expression, scope, branch.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
                        matches = At(branch.Line, () => selector.Kind == ValueKind.Empty || candidate.Kind == ValueKind.Empty ? selector.Kind == candidate.Kind : Expression.Apply("=", selector, candidate, EffectiveSameString).AsNumber() != 0);
                        if (matches)
                        {
                            break;
                        }
                    }
                }

                if (matches)
                {
                    return await BlockAsync(
                        branch.Body, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
            }

            return default;
        }

        private async ValueTask<Flow> ForAsync(
            ForStatement loop, Scope scope)
        {
            double initial = NumberAt(loop.Line,
                (await EvaluateAsync(loop.Start, scope,
                loop.Line).ConfigureAwait(WorkerThreadContext.IsCurrent)));
            Write(scope, loop.Name, Value.Numeric(
                initial), loop.Line);
            double limit = NumberAt(loop.Line,
                (await EvaluateAsync(loop.End, scope,
                loop.Line).ConfigureAwait(WorkerThreadContext.IsCurrent)));
            double step = NumberAt(loop.Line,
                (await EvaluateAsync(loop.Step, scope,
                loop.Line).ConfigureAwait(WorkerThreadContext.IsCurrent)));
            if (step == 0)
            {
                throw new ScriptException(loop.Line,
                    "STEPに0は指定できません。");
            }

            while (true)
            {
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                double counter = At(loop.Line,
                    () => NumericOperations.Number(scope.Read(loop.Name)));
                if (step > 0 ? counter > limit
                    : counter < limit)
                {
                    return default;
                }

                Flow result = await BlockAsync(
                    loop.Body, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                if (LeaveLoop(result,
                    out Flow propagated))
                {
                    return propagated;
                }

                double current = At(loop.Line,
                    () => NumericOperations.Number(scope.Read(loop.Name)));
                double next = At(loop.Line, () => NumericArithmetic?.Evaluate(
                    NumericBinaryOperation.Add, current, step) ?? current + step);
                if (next == current)
                {
                    throw new ScriptException(
                        loop.Line, "数値精度のためFORの反復変数を更新できません。");
                }

                Write(scope, loop.Name, At(
                    loop.Line, () => Value.Numeric(next)), loop.Line);
            }
        }

        private async ValueTask<Flow> ForInAsync(
            ForInStatement loop, Scope scope)
        {
            Value collection = await EvaluateAsync(
                loop.Collection, scope, loop.Line).ConfigureAwait(WorkerThreadContext.IsCurrent);
            if (loop.Collection is CallExpr call
                && CollectionVariables.TryGetValue(call.Name, out string? variable))
            {
                collection = scope.Read(variable);
            }

            IReadOnlyList<Value> collected;
            if (collection.Kind == ValueKind.Object)
            {
                if (windows is null)
                {
                    throw new ScriptException(loop.Line, "COM操作adapterが設定されていません。");
                }

                collected = await WindowsObjectCallAsync(async () =>
                {
                    return CollectForIn(await windows.EnumerateObjectsAsync(
                        collection, token).ConfigureAwait(WorkerThreadContext.IsCurrent));
                }, () => (IReadOnlyList<Value>)[], loop.Line)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            else if (collection.Kind == ValueKind.HashTable)
            {
                collected = CollectForIn(collection.AsHashTable().Keys().Select(Value.String));
            }
            else if (collection.Kind == ValueKind.Array)
            {
                // 配列は変更不可なので、同じ値列を安全に保持できます。
                collected = collection.AsArray().Values;
            }
            else if (collection.Kind == ValueKind.String)
            {
                var characters = new List<Value>();
                foreach (Rune rune in collection.Text.EnumerateRunes())
                {
                    token.ThrowIfCancellationRequested();
                    characters.Add(Value.String(rune.ToString()));
                }

                collected = characters;
            }
            else
            {
                throw new ScriptException(loop.Line,
                    "FOR INには文字列または配列を指定してください。");
            }

            foreach (Value element in collected)
            {
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                Write(scope, loop.Name, element,
                    loop.Line);
                Flow result = await BlockAsync(
                    loop.Body, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                if (LeaveLoop(result,
                    out Flow propagated))
                {
                    return propagated;
                }
            }

            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            Write(scope, loop.Name, Value.Empty, loop.Line);
            return default;
        }

        private IReadOnlyList<Value> CollectForIn(IEnumerable<Value> values)
        {
            token.ThrowIfCancellationRequested();
            IEnumerator<Value> enumerator = values.GetEnumerator();
            Exception? primary = null;
            try
            {
                var collected = new List<Value>();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    bool hasNext = enumerator.MoveNext();
                    token.ThrowIfCancellationRequested();
                    if (!hasNext)
                    {
                        return collected;
                    }

                    if (collected.Count == UwscArray.MaximumElements)
                    {
                        throw new InvalidOperationException("配列の要素数が上限を超えました。");
                    }

                    Value value = enumerator.Current;
                    token.ThrowIfCancellationRequested();
                    collected.Add(value);
                }
            }
            catch (Exception ex)
            {
                primary = ex;
                throw;
            }
            finally
            {
                try
                {
                    enumerator.Dispose();
                }
                catch (Exception cleanup) when (primary is not null)
                {
                    const string key = "UWSCSharp.ForInCleanupErrors";
                    primary.Data[key] = primary.Data[key] is Exception previous
                        ? new AggregateException(previous, cleanup) : cleanup;
                }
            }
        }

        private async ValueTask<Flow> TryAsync(
            TryStatement attempt, Scope scope)
        {
            scope.ClearTryInformation();
            if (!attempt.Finally)
            {
                try
                {
                    return await BlockAsync(
                        attempt.Body, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                catch (ScriptException ex)
                {
                    scope.SetTryInformation(ex);
                    return await BlockAsync(
                        attempt.Handler, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
            }

            Flow pending = default;
            ExceptionDispatchInfo? failure = null;
            try
            {
                pending = await BlockAsync(
                    attempt.Body, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(
                    ex);
            }

            if (failure?.SourceException is ScriptException scriptFailure)
            {
                scope.SetTryInformation(scriptFailure);
            }

            Flow cleanup = await RunFinallyAsync(
                attempt.Handler, scope, failure).ConfigureAwait(WorkerThreadContext.IsCurrent);
            if (failure?.SourceException is StopSignal)
            {
                failure.Throw();
            }

            if (cleanup.Kind != FlowKind.Normal)
            {
                return cleanup;
            }

            failure?.Throw();
            return pending;
        }

        private static bool LeaveLoop(Flow result,
            out Flow propagated)
        {
            propagated = result;
            if (result.Kind is FlowKind.Break or FlowKind.Continue)
            {
                propagated = result.Levels == 1 ? default
                    : result with
                    {
                        Levels = result.Levels - 1
                    };
                return result.Kind == FlowKind.Break
                    || result.Levels > 1;
            }

            return result.Kind != FlowKind.Normal;
        }
    }
}
