using System.Runtime.ExceptionServices;

namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed partial class Execution
    {
        private ScriptHotkeyRegistry? hotkeys;
        private ExceptionDispatchInfo? hotkeyFailure;
        private bool dispatchingHotkey;

        private async ValueTask<Value> InvokeSetHotkeyAsync(CallExpr call, Scope caller)
        {
            if (call.Arguments.Count is < 1 or > 3)
            {
                throw new InvalidOperationException($"{BuiltinFunctionNames.SetHotkey}には1～3引数を指定してください。");
            }

            if (State.Control is not null || finallyDepth != 0)
            {
                throw new InvalidOperationException($"{BuiltinFunctionNames.SetHotkey}はメインの実行経路で使用してください。");
            }

            List<Value> values = await BuiltinInvocation.EvaluateArgumentsAsync(
                call.Arguments, caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            int key = call.Arguments[0] is OmittedExpr ? 0 : ReadCode(values[0], 255);
            int modifiers = values.Count > 1 && call.Arguments[1] is not OmittedExpr
                ? ReadCode(values[1], 15) : 0;
            var hotkey = new ScriptHotkey(key, modifiers);
            bool remove = values.Count < 3 || call.Arguments[2] is OmittedExpr;
            Routine? routine = null;
            ModuleState? module = caller.Module;
            if (!remove)
            {
                if (values[2].Kind != ValueKind.String)
                {
                    throw new InvalidOperationException("Procedure名は文字列で指定してください。");
                }

                string name = values[2].Display();
                if (module is null || !module.Definition.Routines.TryGetValue(name, out routine))
                {
                    module = null;
                    TryGetGlobalRoutine(name, out routine);
                }

                if (routine is null || routine.ReturnsValue
                    || routine.Parameters.Any(parameter => parameter.Default is null))
                {
                    throw new InvalidOperationException("引数なしで呼び出せるProcedureを指定してください。");
                }
            }

            if (SelectiveInputEmergencyShortcut.IsReserved(hotkey))
            {
                return Value.Logical(false);
            }

            if (hotkeys is null)
            {
                if (rootWindows is null)
                {
                    throw new InvalidOperationException("この実行先はホットキー登録に対応していません。");
                }

                State.Token = runCancellation.Token;
                windows?.ConfigurePause(pauseControl, token);
                hotkeys = new ScriptHotkeyRegistry(rootWindows.CreateHotkeySource(),
                    pendingHotkeyLimit, () => Interlocked.CompareExchange(ref hotkeyFailure,
                        ExceptionDispatchInfo.Capture(new InvalidOperationException(
                            "ホットキー通知の保留上限を超えました。")), null));
            }

            bool result = remove ? hotkeys.TryUnregister(hotkey)
                : hotkeys.TryRegister(hotkey, async (pressed, active) =>
                {
                    Dictionary<string, Value> snapshots = State.Snapshots;
                    bool hadKey = snapshots.TryGetValue(BuiltinVariableNames.HotkeyVk, out Value previousKey);
                    bool hadModifiers = snapshots.TryGetValue(BuiltinVariableNames.HotkeyMod, out Value previousModifiers);
                    snapshots[BuiltinVariableNames.HotkeyVk] = Value.Numeric(pressed.VirtualKey);
                    snapshots[BuiltinVariableNames.HotkeyMod] = Value.Numeric(pressed.Modifiers);
                    try
                    {
                        var scope = new Scope(this) { Module = module, SourcePath = caller.SourcePath };
                        await InvokeRoutineDefinitionAsync(new CallExpr(values[2].Display(), []),
                            scope, routine!, module).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }
                    finally
                    {
                        Restore(BuiltinVariableNames.HotkeyVk, hadKey, previousKey);
                        Restore(BuiltinVariableNames.HotkeyMod, hadModifiers, previousModifiers);
                    }

                    void Restore(string name, bool existed, Value value)
                    {
                        if (existed)
                        {
                            snapshots[name] = value;
                        }
                        else
                        {
                            snapshots.Remove(name);
                        }
                    }
                });
            return Value.Logical(result);

            static int ReadCode(Value value, int maximum)
            {
                double number = NumericOperations.RuntimeNumber(value);
                if (!double.IsFinite(number) || number != Math.Truncate(number)
                    || number < 0 || number > maximum)
                {
                    throw new InvalidOperationException("ホットキーのキーコードまたは修飾子が範囲外です。");
                }

                return (int)number;
            }
        }

        private async ValueTask DispatchHotkeyAsync()
        {
            if (State.Control is not null || finallyDepth != 0)
            {
                return;
            }

            if (Volatile.Read(ref hotkeyFailure) is { } failure)
            {
                await runCancellation.CancelAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                failure.Throw();
            }

            if (hotkeys is null)
            {
                return;
            }

            try
            {
                hotkeys.ThrowIfFaulted();
                if (dispatchingHotkey || (pauseControl?.IsPaused ?? false))
                {
                    return;
                }

                dispatchingHotkey = true;
                try
                {
                    await hotkeys.DispatchOneAsync(token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                finally
                {
                    dispatchingHotkey = false;
                }
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException)
                {
                    Interlocked.CompareExchange(ref hotkeyFailure,
                        ExceptionDispatchInfo.Capture(error), null);
                }

                // 配送失敗でも、既に開始したホスト待機を回収できるよう取消を伝えます。
                await runCancellation.CancelAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                throw;
            }
        }
    }
}
