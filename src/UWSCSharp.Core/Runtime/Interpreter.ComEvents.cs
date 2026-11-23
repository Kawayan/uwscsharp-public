using System.Runtime.CompilerServices;

namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private readonly ConditionalWeakTable<Exception, object> comCallbackFailures = new();

        private bool IsComCallbackScriptError(Exception failure) =>
            failure is ScriptException && comCallbackFailures.TryGetValue(failure, out _);

        private async ValueTask<Value> InvokeOleEventAsync(CallExpr call, Scope caller)
        {
            if (call.Arguments.Count is not (1 or 4)
                || call.Arguments.Any(argument => argument is OmittedExpr))
            {
                throw new InvalidOperationException(
                    $"{BuiltinFunctionNames.OleEvent}には1引数または4引数を指定してください。");
            }

            List<Value> values = await BuiltinInvocation.EvaluateArgumentsAsync(
                call.Arguments, caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            if (values[0].Kind != ValueKind.Object)
            {
                throw new InvalidOperationException("COMオブジェクトを指定してください。");
            }

            IWindowsAutomationSession session = windows
                ?? throw MissingComAdapter();
            if (values.Count == 1)
            {
                return await WindowsObjectCallAsync(async () =>
                {
                    await session.SetComEventAsync(values[0], null, null, null, token)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    return Value.String("");
                }, () => ComErrorValue).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            if (values.Skip(1).Any(value => value.Kind != ValueKind.String
                || value.Text.Length == 0))
            {
                throw new InvalidOperationException(
                    "インタフェース名・イベント名・Procedure名を空でない文字列で指定してください。");
            }

            Routine? routine = null;
            ModuleState? module = caller.Module;
            if (module is null || !module.Definition.Routines.TryGetValue(values[3].Text, out routine))
            {
                module = null;
                TryGetGlobalRoutine(values[3].Text, out routine);
            }

            if (routine is null || routine.ReturnsValue
                || routine.Parameters.Any(parameter => parameter.Default is null))
            {
                throw new InvalidOperationException(
                    "引数なしで呼び出せるProcedureを指定してください。");
            }

            ExecutionThreadState owner = State;
            ScriptComEventDispatcher dispatcher = owner.ComEvents ??= new();
            return await WindowsObjectCallAsync(async () =>
            {
                await session.SetComEventAsync(values[0], values[1].Text, values[2].Text,
                    (arguments, active) => EnqueueComEventAsync(owner, dispatcher, async () =>
                    {
                        Dictionary<string, Value> snapshots = owner.Snapshots;
                        bool existed = snapshots.TryGetValue(
                            BuiltinVariableNames.EventPrm, out Value previous);
                        snapshots[BuiltinVariableNames.EventPrm] = Value.FromArray(
                            UwscArray.FromValues(arguments));
                        try
                        {
                            var scope = new Scope(this)
                            {
                                Module = module,
                                SourcePath = caller.SourcePath
                            };
                            await InvokeRoutineDefinitionAsync(new CallExpr(values[3].Text, []),
                                scope, routine, module).ConfigureAwait(WorkerThreadContext.IsCurrent);
                            Value result = snapshots[BuiltinVariableNames.EventPrm];
                            if (result.Kind != ValueKind.Array || result.AsArray().Rank != 1
                                || result.AsArray().LowerBounds[0] != 0
                                || result.AsArray().Count != arguments.Count)
                            {
                                throw new ScriptException(caller.CurrentLine,
                                    "EVENT_PRMの次元・下限・要素数は変更できません。");
                            }

                            return result.AsArray().Values;
                        }
                        catch (Exception failure)
                        {
                            comCallbackFailures.GetValue(failure, static _ => new object());
                            throw;
                        }
                        finally
                        {
                            if (existed)
                            {
                                snapshots[BuiltinVariableNames.EventPrm] = previous;
                            }
                            else
                            {
                                snapshots.Remove(BuiltinVariableNames.EventPrm);
                            }
                        }
                    }), token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                return Value.String("");
            }, () => ComErrorValue).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        private static async ValueTask<IReadOnlyList<Value>> EnqueueComEventAsync(
            ExecutionThreadState owner, ScriptComEventDispatcher dispatcher,
            Func<ValueTask<IReadOnlyList<Value>>> callback)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                owner.LifetimeToken, owner.Token);
            return await dispatcher.EnqueueAsync(callback, cancellation.Token)
                .ConfigureAwait(false);
        }

        private ValueTask DispatchComEventAsync()
        {
            if (finallyDepth != 0 || (pauseControl?.IsPaused ?? false)
                || (State.Control?.IsSuspended ?? false))
            {
                return ValueTask.CompletedTask;
            }

            return State.ComEvents?.DispatchOneAsync() ?? ValueTask.CompletedTask;
        }
    }
}
