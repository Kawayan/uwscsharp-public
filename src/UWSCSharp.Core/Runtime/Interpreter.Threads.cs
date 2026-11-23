using System.Runtime.ExceptionServices;

namespace UWSCSharp.Core;

public sealed partial class Interpreter
{
    private sealed class NativeDllRoutineFrame(int writeBackDepth)
    {
        internal int WriteBackDepth { get; } = writeBackDepth;

        internal Exception? CopybackFailure;
    }

    private sealed class NativeDllWriteBackContext
    {
        internal ExceptionDispatchInfo? CopybackFailure;
    }

    private sealed class ExecutionThreadState(CancellationToken token,
        IWindowsAutomationSession? windows)
    {
        // 通知元スレッドへ現在の待機tokenを参照単位で公開します。
        private sealed record CurrentToken(CancellationToken Value);
        private CurrentToken currentToken = new(token);

        internal CancellationToken Token
        {
            get => Volatile.Read(ref currentToken).Value;
            set => Volatile.Write(ref currentToken, new CurrentToken(value));
        }
        internal CancellationToken LifetimeToken { get; init; } = token;
        internal IWindowsAutomationSession? Windows = windows;
        internal INumericArithmetic? NumericArithmetic;
        internal readonly PrintFormatter Formatter = new();
        internal CancellationTokenSource? FinallyCancellation;
        internal int FinallyDepth;
        internal int NativeDllWriteBackDepth;
        internal NativeDllWriteBackContext? NativeDllWriteBack;
        internal NativeDllRoutineFrame? NativeDllRoutine;
        internal int FinallySteps;
        internal int EvaluationDepth;
        internal bool IgnoreComErrors;
        internal bool ComErrorOccurred;
        internal ScriptComEventDispatcher? ComEvents;
        internal WorkerThreadControl? Control;
        internal Value ParameterArguments = Value.FromArray(UwscArray.FromValues([]));
        internal readonly Dictionary<string, Value> Snapshots = new(
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed partial class Execution
    {
        private readonly CancellationTokenSource runCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private readonly object workersGate = new();
        internal readonly object sharedVariablesGate = new();
        private readonly SemaphoreSlim outputGate = new(1, 1);
        private readonly WorkerThreadControl?[] workers = new WorkerThreadControl[64];
        private bool workersClosing;
        private volatile bool mainEndedByScript;
        private volatile ExceptionDispatchInfo? workerFailure;
        private volatile StopSignal? workerExit;
        private readonly ConcurrentScriptFileSession? sharedFiles =
            files is null ? null : new(files);

        internal bool TryReadThreadVariable(string name, out Value value)
        {
            if (name.Equals(BuiltinVariableNames.ComErrFlg,
                StringComparison.OrdinalIgnoreCase))
            {
                value = Value.Logical(State.ComErrorOccurred);
                return true;
            }

            if (name.Equals(BuiltinVariableNames.ParamStr,
                StringComparison.OrdinalIgnoreCase))
            {
                value = State.ParameterArguments;
                return true;
            }

            return State.Snapshots.TryGetValue(name, out value);
        }

        internal bool TryWriteThreadVariable(string name, Value value)
        {
            if (name.Equals(BuiltinVariableNames.ParamStr,
                StringComparison.OrdinalIgnoreCase))
            {
                State.ParameterArguments = value;
                return true;
            }

            if (State.Snapshots.ContainsKey(name))
            {
                State.Snapshots[name] = value;
                return true;
            }

            return false;
        }

        private async ValueTask StartThreadAsync(Expr expression, Scope caller)
        {
            CallExpr call;
            ModuleState? module = caller.Module;
            if (expression is MemberExpr member && !member.Indexed)
            {
                Value target = await member.Target.EvaluateAsync(caller, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                ThrowIfStoppedNativeDllRoutine();
                if (target.ObjectReference is not CoreNamespace space)
                {
                    throw new InvalidOperationException(
                        "THREADにはユーザー関数を指定してください。");
                }

                module = space.Module;
                call = new(member.Name, member.Arguments);
            }
            else if (expression is CallExpr routineCall)
            {
                call = routineCall;
            }
            else
            {
                throw new InvalidOperationException(
                    "THREADにはユーザー関数呼出しを指定してください。");
            }

            Routine? routine;
            if (module is null || !module.Definition.Routines.TryGetValue(
                call.Name, out routine))
            {
                module = null;
                if (!TryGetGlobalRoutine(call.Name, out routine))
                {
                    throw new InvalidOperationException($"未定義の関数: {call.Name}");
                }
            }

            int required = routine.Parameters.Count(parameter => parameter.Default is null);
            if (call.Arguments.Count < required
                || call.Arguments.Count > routine.Parameters.Count
                || call.Arguments.Any(argument => argument is NamedExpr))
            {
                throw new InvalidOperationException($"引数の数または形式が不正です: {call.Name}");
            }

            var callee = new Scope(this)
            {
                Module = module,
                HasRoutineResult = true,
                RoutineReturnsValue = routine.ReturnsValue,
                SourcePath = routine.Body.FirstOrDefault()?.SourcePath ?? caller.SourcePath
            };
            callee.Locals[BuiltinVariableNames.Result] = Value.Empty;
            // THREADの引数はVAR宣言でも開始時のコピーとし、書戻しません。
            await BindRoutineArgumentsAsync(call, routine, caller, callee,
                null).ConfigureAwait(WorkerThreadContext.IsCurrent);
            ThrowIfStoppedNativeDllRoutine();

            if (State.Control is null && finallyDepth == 0)
            {
                State.Token = runCancellation.Token;
                windows?.ConfigurePause(pauseControl, State.Token);
            }
            Value parameters = State.ParameterArguments;
            Dictionary<string, Value> snapshots = new(State.Snapshots,
                StringComparer.OrdinalIgnoreCase);
            IWindowsAutomationSession? workerWindows = windows?.Fork();
            IWorkerThreadPriority? priority;
            try
            {
                priority = workerWindows?.CreateWorkerThreadPriority();
            }
            catch (Exception primary)
            {
                if (workerWindows is not null)
                {
                    try
                    {
                        await workerWindows.DisposeAsync();
                    }
                    catch (Exception cleanup)
                    {
                        Exception? retained = primary;
                        AddWorkerCleanup(ref retained, cleanup);
                    }
                }
                throw;
            }
            var control = new WorkerThreadControl(priority);
            int slot = -1;
            bool started = false;
            try
            {
                lock (workersGate)
                {
                    if (workersClosing)
                    {
                        throw new InvalidOperationException(
                            "実行の終了処理中はTHREADを開始できません。");
                    }
                    for (int index = 0; index < workers.Length; index++)
                    {
                        if (workers[index] is { } previous
                            && previous.Completion.IsCompleted)
                        {
                            // 完了とJoinを確認済みなので、ここでは非同期待機しません。
                            previous.DisposeAsync().GetAwaiter().GetResult();
                            workers[index] = null;
                        }

                        if (workers[index] is null)
                        {
                            slot = index;
                            workers[index] = control;
                            break;
                        }
                    }

                    if (slot < 0)
                    {
                        throw new InvalidOperationException("THREADの同時実行数が64件を超えました。");
                    }

                    control.Start(async workerToken =>
                    {
                        var state = new ExecutionThreadState(workerToken, workerWindows)
                        {
                            Control = control,
                            ParameterArguments = parameters
                        };
                        foreach (KeyValuePair<string, Value> snapshot in snapshots)
                        {
                            state.Snapshots.Add(snapshot.Key, snapshot.Value);
                        }

                        threadState.Value = state;
                        INumericArithmeticScope? ownedArithmetic = null;
                        Exception? primary = null;
                        try
                        {
                            ownedArithmetic = (numericArithmetic
                                as IForkableNumericArithmetic)?.ForkForThread();
                            state.NumericArithmetic = ownedArithmetic ?? numericArithmetic;
                            workerWindows?.ConfigurePause(pauseControl, workerToken);
                            await BlockAsync(routine.Body, callee)
                                .ConfigureAwait(WorkerThreadContext.IsCurrent);
                        }
                        catch (Exception failure)
                        {
                            primary = failure;
                        }
                        state.ComEvents?.Close();
                        try
                        {
                            state.FinallyCancellation?.Dispose();
                        }
                        catch (Exception cleanup)
                        {
                            AddWorkerCleanup(ref primary, cleanup);
                        }
                        if (workerWindows is not null)
                        {
                            try
                            {
                                await workerWindows.DisposeAsync()
                                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                            }
                            catch (Exception cleanup)
                            {
                                AddWorkerCleanup(ref primary, cleanup);
                            }
                        }
                        try
                        {
                            ownedArithmetic?.Dispose();
                        }
                        catch (Exception cleanup)
                        {
                            AddWorkerCleanup(ref primary, cleanup);
                        }
                        if (primary is not null)
                        {
                            if (primary is not OperationCanceledException
                                || !workerToken.IsCancellationRequested
                                || primary.Data.Contains("UWSCSharp.WorkerCleanupErrors")
                                || primary.Data.Contains("UWSCSharp.FinallyErrors"))
                            {
                                lock (workersGate)
                                {
                                    if (primary is StopSignal stop)
                                    {
                                        workerExit ??= stop;
                                    }
                                    else
                                    {
                                        workerFailure ??= ExceptionDispatchInfo.Capture(primary);
                                    }
                                }
                                await runCancellation.CancelAsync();
                            }
                            ExceptionDispatchInfo.Capture(primary).Throw();
                        }
                    }, runCancellation.Token);
                    started = true;
                }

                await control.Ready.ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception primary)
            {
                if (slot < 0 || !started || control.Ready.IsFaulted || control.Ready.IsCanceled)
                {
                    try
                    {
                        await control.DisposeAsync();
                    }
                    catch (Exception cleanup)
                    {
                        if (!ReferenceEquals(primary, cleanup))
                        {
                            Exception? retained = primary;
                            AddWorkerCleanup(ref retained, cleanup);
                        }
                    }
                    if (workerWindows is not null)
                    {
                        try
                        {
                            await workerWindows.DisposeAsync();
                        }
                        catch (Exception cleanup)
                        {
                            Exception? retained = primary;
                            AddWorkerCleanup(ref retained, cleanup);
                        }
                    }
                }

                throw;
            }
        }

        private static void AddWorkerCleanup(ref Exception? primary, Exception cleanup)
        {
            if (primary is null)
            {
                primary = cleanup;
                return;
            }
            IEnumerable<Exception> previous = primary.Data["UWSCSharp.WorkerCleanupErrors"]
                is AggregateException aggregate ? aggregate.InnerExceptions : [];
            primary.Data["UWSCSharp.WorkerCleanupErrors"] =
                new AggregateException(previous.Append(cleanup));
        }

        private async ValueTask<Value> InvokeCtrlTrdAsync(CallExpr call, Scope caller)
        {
            if (call.Arguments.Count != 2
                || call.Arguments.Any(argument => argument is OmittedExpr))
            {
                throw new InvalidOperationException("CTRLTRDにはIDと制御方法を指定してください。");
            }

            Value threadId = await call.Arguments[0].EvaluateAsync(caller, token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            ThrowIfStoppedNativeDllRoutine();
            int id = NumericOperations.Integer(threadId);

            Value controlMode = await call.Arguments[1].EvaluateAsync(caller, token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            ThrowIfStoppedNativeDllRoutine();
            int mode = NumericOperations.Integer(controlMode);
            WorkerThreadControl? control;
            lock (workersGate)
            {
                control = id >= 0 && id < workers.Length ? workers[id] : null;
            }

            control?.Apply(mode);
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            return Value.String("");
        }

        private async ValueTask StopWorkersAsync(Exception? primary)
        {
            WorkerThreadControl[] pending;
            lock (workersGate)
            {
                workersClosing = true;
                mainEndedByScript = (primary is null
                        || primary is StopSignal && primary.Data["UWSCSharp.FinallyErrors"] is null)
                    && !runCancellation.IsCancellationRequested
                    && workerFailure is null && workerExit is null;
                pending = [.. workers.OfType<WorkerThreadControl>()];
            }

            List<Exception> errors = [];
            try
            {
                await runCancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                errors.Add(failure);
            }
            foreach (WorkerThreadControl control in pending)
            {
                try
                {
                    control.Apply((int)WorkerThreadCommand.Quit);
                }
                catch (Exception failure)
                {
                    errors.Add(failure);
                }
            }

            foreach (WorkerThreadControl control in pending)
            {
                try
                {
                    await control.DisposeAsync().ConfigureAwait(false);
                }
                catch (StopSignal stop)
                {
                    foreach (string key in new[] { "UWSCSharp.WorkerCleanupErrors",
                        "UWSCSharp.FinallyErrors" })
                    {
                        if (stop.Data[key] is Exception cleanup)
                        {
                            errors.Add(cleanup);
                        }
                    }
                }
                catch (Exception failure)
                {
                    if (!ReferenceEquals(failure, primary)
                        && !(primary is null
                            && ReferenceEquals(failure, workerFailure?.SourceException)))
                    {
                        errors.Add(failure);
                    }
                }
            }

            runCancellation.Dispose();
            if (primary is not null && errors.Count > 0)
            {
                primary.Data["UWSCSharp.WorkerCleanupErrors"] = new AggregateException(errors);
            }
            else if (errors.Count > 0)
            {
                throw new AggregateException("THREADの終了処理に失敗しました。", errors);
            }

            if (primary is null)
            {
                workerFailure?.Throw();
                if (workerExit is not null)
                {
                    throw workerExit;
                }
            }
        }
    }
}
