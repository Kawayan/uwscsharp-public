using System.Runtime.ExceptionServices;
using System.Text;

namespace UWSCSharp.Core;

/// <summary>
/// スクリプトを実行し、ホストを通じて外部操作を行います。
/// </summary>
public sealed partial class Interpreter(UwscRandom? random = null,
    IWindowsAutomation? windows = null,
    IScriptFiles? files = null,
    TimeProvider? timeProvider = null) : IScriptRunner
{
    /// <summary>
    /// スクリプトを独立した実行状態で非同期に実行します。
    /// </summary>
    public Task RunAsync(string source,
        Action<string> output,
        CancellationToken cancellationToken = default) => RunAsync(
        source, new CallbackScriptHost(output), cancellationToken);

    /// <summary>
    /// 起動引数を指定してスクリプトを実行します。
    /// </summary>
    public Task RunAsync(string source, Action<string> output,
        CancellationToken cancellationToken, ScriptRunOptions options) =>
        RunAsync(source, new CallbackScriptHost(output),
            cancellationToken, options);

    /// <summary>
    /// スクリプトを独立した実行状態で非同期に実行します。
    /// </summary>
    public Task RunAsync(string source, IScriptHost host,
        CancellationToken cancellationToken = default) =>
        RunAsync(source, host, cancellationToken, ScriptRunOptions.Empty);

    /// <summary>
    /// ホストと起動引数を指定してスクリプトを実行します。
    /// </summary>
    public Task RunAsync(string source, IScriptHost host,
        CancellationToken cancellationToken, ScriptRunOptions options)
        => RunWithExitCodeAsync(source, host, cancellationToken, options);

    /// <summary>
    /// スクリプトを実行し、EXITEXITの終了コードを返します。
    /// </summary>
    public Task<int> RunWithExitCodeAsync(string source, IScriptHost host,
        CancellationToken cancellationToken, ScriptRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        return RunWithExitCodeAsync(ScriptCompiler.CompileWithContext(source,
            options.SourcePath, true, cancellationToken), host, cancellationToken, options);
    }

    /// <summary>
    /// スクリプトを独立した実行状態で非同期に実行します。
    /// </summary>
    public Task RunAsync(ScriptProgram program,
        IScriptHost host, CancellationToken cancellationToken = default) =>
        RunAsync(program, host, cancellationToken, ScriptRunOptions.Empty);

    /// <summary>
    /// コンパイル済みプログラムを指定の起動引数で実行します。
    /// </summary>
    public Task RunAsync(ScriptProgram program, IScriptHost host,
        CancellationToken cancellationToken, ScriptRunOptions options) =>
        RunWithExitCodeAsync(program, host, cancellationToken, options);

    /// <summary>
    /// コンパイル済みプログラムを実行し、終了コードを返します。
    /// </summary>
    public async Task<int> RunWithExitCodeAsync(ScriptProgram program, IScriptHost host,
        CancellationToken cancellationToken, ScriptRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        IWindowsAutomationSession? session = null;
        IScriptFileSession? fileSession = null;
        INumericArithmeticScope? numericScope = null;
        CancellationToken backendStopToken = CancellationToken.None;
        CancellationToken executionToken = cancellationToken;
        CancellationTokenSource? executionCancellation = null;
        Exception? primary = null;
        int exitCode = 0;
        List<Exception> cleanupErrors = [];
        var stopOptions = new StopFormOptionsScope(host);
        Action? afterCleanup = null;
        try
        {
            session = windows?.CreateSession();
            backendStopToken = session?.ExecutionStopToken ?? CancellationToken.None;
            if (!cancellationToken.CanBeCanceled)
            {
                executionToken = backendStopToken;
            }
            else if (backendStopToken.CanBeCanceled && backendStopToken != cancellationToken)
            {
                executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, backendStopToken);
                executionToken = executionCancellation.Token;
            }

            if (backendStopToken.IsCancellationRequested)
            {
                executionToken.ThrowIfCancellationRequested();
            }
            session?.ConfigurePause(options.PauseControl, executionToken);
            if (options.DefaultFont is { } defaultFont)
            {
                session?.ConfigureDialogOption(Keywords.DefaultFont,
                    [Value.String(defaultFont)], executionToken);
            }

            fileSession = files?.CreateSession();
            if (options.NumericArithmetic is null
                && windows is INumericArithmeticProvider provider)
            {
                numericScope = provider.CreateNumericArithmetic();
            }
            await new Execution(program, host,
                executionToken, random ?? UwscRandom.Shared,
                session, fileSession, options.Arguments,
                ScriptExecutionContext.Create(program, options),
                timeProvider ?? TimeProvider.System,
                options.PauseControl, stopOptions, options.PendingHotkeyLimit,
                options.NumericArithmetic ?? numericScope, options.PowerOperation)
                .RunAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
        }
        catch (StopSignal stop)
        {
            exitCode = stop.ExitCode;
            afterCleanup = stop.AfterCleanup;
            foreach (string key in new[] { "UWSCSharp.WorkerCleanupErrors",
                "UWSCSharp.FinallyErrors" })
            {
                if (stop.Data[key] is Exception cleanup)
                {
                    cleanupErrors.Add(cleanup);
                }
            }
        }
        catch (HostFailure ex)
        {
            primary = ex.InnerException!;
            foreach (System.Collections.DictionaryEntry item in ex.Data)
            {
                if (primary.Data[item.Key] is Exception previous
                    && item.Value is Exception additional)
                {
                    primary.Data[item.Key] = new AggregateException(previous, additional);
                }
                else
                {
                    primary.Data[item.Key] = item.Value;
                }
            }
        }
        catch (Exception ex)
        {
            primary = ex;
        }
        finally
        {
            // 作成の逆順に全sessionの解放を試み、後続の解放も省略しません。
            await ReleaseAsync(stopOptions).ConfigureAwait(WorkerThreadContext.IsCurrent);
            await ReleaseAsync(numericScope).ConfigureAwait(WorkerThreadContext.IsCurrent);
            await ReleaseAsync(fileSession).ConfigureAwait(WorkerThreadContext.IsCurrent);
            await ReleaseAsync(session).ConfigureAwait(WorkerThreadContext.IsCurrent);
            executionCancellation?.Dispose();
        }

        if (primary is not null)
        {
            if (primary is OperationCanceledException cancelled
                && cancelled.CancellationToken == executionToken)
            {
                CancellationToken origin = cancellationToken.IsCancellationRequested
                    ? cancellationToken
                    : backendStopToken.IsCancellationRequested ? backendStopToken : executionToken;
                if (origin != executionToken)
                {
                    // 内部の結合tokenを外へ返さず、通知元のtokenと診断を保持します。
                    OperationCanceledException normalized = cancelled is TaskCanceledException
                        ? new TaskCanceledException(cancelled.Message, cancelled, origin)
                        : new OperationCanceledException(cancelled.Message, cancelled, origin);
                    foreach (System.Collections.DictionaryEntry item in cancelled.Data)
                    {
                        normalized.Data[item.Key] = item.Value;
                    }

                    primary = normalized;
                }
            }

            if (cleanupErrors.Count > 0)
            {
                // 元の例外型・取消tokenを維持し、解放失敗も呼出し元へ残します。
                primary.Data["UWSCSharp.CleanupErrors"] = new AggregateException(cleanupErrors);
            }

            ExceptionDispatchInfo.Capture(primary).Throw();
        }

        if (cleanupErrors.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupErrors[0]).Throw();
        }

        if (cleanupErrors.Count > 1)
        {
            throw new AggregateException("複数のsessionの解放に失敗しました。", cleanupErrors);
        }

        if (afterCleanup is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            afterCleanup();
        }

        return exitCode;

        async ValueTask ReleaseAsync(object? resource)
        {
            try
            {
                if (resource is IAsyncDisposable asyncResource)
                {
                    await asyncResource.DisposeAsync()
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                else if (resource is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
            }
        }
    }

    private sealed class StopSignal(int exitCode, Action? afterCleanup = null)
        : Exception
    {
        internal int ExitCode { get; } = exitCode;

        internal Action? AfterCleanup { get; } = afterCleanup;
    }
    private sealed class HostFailure(
        Exception cause) : Exception("Script host failed", cause);
    private sealed partial class Execution(ScriptProgram program,
        IScriptHost host, CancellationToken cancellationToken,
        UwscRandom random, IWindowsAutomationSession? rootWindows,
        IScriptFileSession? files, IReadOnlyList<string> arguments,
        ScriptExecutionContext executionContext, TimeProvider clock,
        ScriptPauseControl? pauseControl, StopFormOptionsScope stopOptions,
        int pendingHotkeyLimit, INumericArithmetic? numericArithmetic,
        Func<PowerCommandRequest, Action?>? powerOperation)
    {
        private static readonly TimeSpan FinallyCancellationTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan WindowsCompletionPollInterval = TimeSpan.FromMilliseconds(20);

        internal bool HasPowerOperation => powerOperation is not null;

        internal INumericArithmetic? NumericArithmetic
        {
            get
            {
                INumericArithmetic? arithmetic = State.NumericArithmetic;
                return arithmetic is INumericControlArithmetic { IsActive: false }
                    ? null : arithmetic;
            }
        }

        private static readonly Dictionary<string, string> CollectionVariables = new(
            StringComparer.OrdinalIgnoreCase)
        {
            [BuiltinFunctionNames.GetDir] = BuiltinVariableNames.GetdirFiles,
            [BuiltinFunctionNames.GetItem] = BuiltinVariableNames.AllItemList,
            [BuiltinFunctionNames.GetAllWin] = BuiltinVariableNames.AllWinId,
            [BuiltinFunctionNames.GetOleItem] = BuiltinVariableNames.AllOleItem
        };

        private readonly NativeDllRegistry nativeDlls = new(program.NativeDllDeclarations);
        private readonly AsyncLocal<ExecutionThreadState?> threadState = new();
        private ExecutionThreadState State => threadState.Value ??=
            new(cancellationToken, rootWindows)
            {
                LifetimeToken = runCancellation.Token,
                NumericArithmetic = numericArithmetic
            };
        private CancellationToken token
        {
            get => State.Token;
            set => State.Token = value;
        }
        private IWindowsAutomationSession? windows => State.Windows;
        private PrintFormatter printFormatter => State.Formatter;
        private CancellationTokenSource? finallyCancellation
        {
            get => State.FinallyCancellation;
            set => State.FinallyCancellation = value;
        }
        private int finallyDepth
        {
            get => State.FinallyDepth;
            set => State.FinallyDepth = value;
        }
        private int finallySteps
        {
            get => State.FinallySteps;
            set => State.FinallySteps = value;
        }
        private int evaluationDepth
        {
            get => State.EvaluationDepth;
            set => State.EvaluationDepth = value;
        }

        internal bool SameString => EffectiveSameString;

        internal bool ExplicitVariables => EffectiveExplicitVariables;

        private async ValueTask<Flow> RunFinallyAsync(
            IReadOnlyList<Statement> statements, Scope scope,
            ExceptionDispatchInfo? pending)
        {
            bool stopping = pending?.SourceException is OperationCanceledException
                || (runCancellation.IsCancellationRequested
                    && (workerFailure is not null || workerExit is not null));
            if (!EffectiveOptFinally || (!stopping && finallyDepth == 0))
            {
                return await BlockAsync(statements, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            CancellationToken saved = token;
            CancellationTokenSource? nativeRoutineFinally = null;
            int savedFinallySteps = finallySteps;
            finallyDepth++;
            try
            {
                if (finallyCancellation is null)
                {
                    // 取消後の処理は別tokenで実行し、停止しないscriptを制限します。
                    if (State.NativeDllWriteBackDepth > 0
                        && State.NativeDllRoutine is not null)
                    {
                        // 添字関数のFINALLYも、外側の書戻しの期限を共有します。
                        nativeRoutineFinally = CancellationTokenSource.CreateLinkedTokenSource(saved);
                        finallyCancellation = nativeRoutineFinally;
                    }
                    else
                    {
                        finallyCancellation = new CancellationTokenSource(FinallyCancellationTimeout, clock);
                    }
                    finallySteps = 10000;
                }

                token = finallyCancellation.Token;
                Flow result = await BlockAsync(statements, scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
                if (pending?.SourceException is OperationCanceledException)
                {
                    pending.Throw();
                }

                return result;
            }
            catch (Exception error) when (stopping && pending is not null
                && !ReferenceEquals(error, pending.SourceException))
            {
                Exception primary = pending.SourceException;
                RecordFinallyError(primary, error);
                pending.Throw();
                throw;
            }
            finally
            {
                token = saved;
                finallyDepth--;
                if (nativeRoutineFinally is not null)
                {
                    finallyCancellation = null;
                    finallySteps = savedFinallySteps;
                    nativeRoutineFinally.Dispose();
                }
            }
        }

        private static void RecordFinallyError(Exception primary, Exception error)
        {
            IEnumerable<Exception> errors = primary.Data["UWSCSharp.FinallyErrors"]
                is AggregateException previous ? previous.InnerExceptions : [];
            primary.Data["UWSCSharp.FinallyErrors"] = new AggregateException(errors.Append(error));
        }

        private async ValueTask ObserveCancellationAsync()
        {
            if (State.NativeDllWriteBackDepth > 0 && finallyDepth == 0)
            {
                // 書戻し中は停止・一時停止と通知の再入を後回しにします。
                token.ThrowIfCancellationRequested();
                ThrowIfAutomationFailed();
                ThrowIfStoppedNativeDllRoutine();
                return;
            }
            if (State.Control is null && finallyDepth == 0)
            {
                workerFailure?.Throw();
                if (workerExit is not null)
                {
                    throw workerExit;
                }
            }
            if (finallyDepth == 0 && State.Control is not null)
            {
                await State.Control.CheckpointAsync(token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            token.ThrowIfCancellationRequested();
            ThrowIfAutomationFailed();
            await DispatchHotkeyAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            await DispatchComEventAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            if (windows?.RequiresInputPolling == true)
            {
                await HostCallAsync(() =>
                {
                    windows.PollInput(token);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            // FINALLYや解放処理は一時停止で妨げません。
            if (finallyDepth == 0 && pauseControl is not null
                && (windows?.CanPause ?? true))
            {
                await WaitWithAutomationFailureAsync(() => pauseControl.WaitAsync(token))
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            if (finallyDepth > 0 && --finallySteps < 0)
            {
                throw new InvalidOperationException("取消後のFINALLY確認回数が上限を超えました。");
            }
        }

        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, Value> globals = new(
            StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> constants = new(
            StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// スクリプトを独立した実行状態で非同期に実行します。
        /// </summary>
        public async Task RunAsync()
        {
            rootWindows?.ConfigurePause(pauseControl, token);
            rootWindows?.ConfigureDialogContext(executionContext.ScriptName);
            var main = new Scope(this) { SourcePath = program.InitializationFiles.FirstOrDefault() };
            InitializeBuiltinGlobals();

            ValueTask InitializeGlobalAsync(GlobalDeclaration declaration) =>
                InitializeGlobalDeclarationAsync(declaration, main);

            Exception? executionFailure = null;
            try
            {
                await InitializeDeclarationsAsync(main,
                    InitializeGlobalAsync).ConfigureAwait(WorkerThreadContext.IsCurrent);
                await BlockAsync(program.Main,
                    main).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception failure)
            {
                if (failure is OperationCanceledException canceled
                    && cancellationToken.IsCancellationRequested
                    && canceled.CancellationToken != cancellationToken)
                {
                    var external = new OperationCanceledException(
                        canceled.Message, canceled, cancellationToken);
                    foreach (System.Collections.DictionaryEntry item in canceled.Data)
                    {
                        external.Data[item.Key] = item.Value;
                    }
                    executionFailure = external;
                    throw external;
                }
                if (failure is OperationCanceledException)
                {
                    if (hotkeyFailure is not null)
                    {
                        executionFailure = hotkeyFailure.SourceException;
                        hotkeyFailure.Throw();
                    }

                    if (workerFailure is not null)
                    {
                        executionFailure = workerFailure.SourceException;
                        workerFailure.Throw();
                    }
                    if (workerExit is not null)
                    {
                        executionFailure = workerExit;
                        throw workerExit;
                    }
                }
                executionFailure = failure;
                throw;
            }
            finally
            {
                await CompleteRunAsync(executionFailure)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
        }

        private void InitializeBuiltinGlobals()
        {
            globals[BuiltinVariableNames.GetAppdataDir] = Value.String(
                executionContext.ApplicationDataDirectory);
            globals[BuiltinVariableNames.GetWinDir] = Value.String(
                executionContext.WindowsDirectory);
            globals[BuiltinVariableNames.GetSysDir] = Value.String(
                executionContext.SystemDirectory);
            globals[BuiltinVariableNames.GetUwscDir] = Value.String(
                executionContext.ExecutableDirectory);
            globals[BuiltinVariableNames.GetUwscName] = Value.String(
                executionContext.ScriptName);
            globals[BuiltinVariableNames.GetUwscVer] = Value.String(
                ScriptRuntimeInformation.Version);
            globals[BuiltinVariableNames.GetUwscPro] = Value.Numeric(0);
            State.ParameterArguments = StringArray(arguments);
            AddImageDefault(BuiltinVariableNames.GImgX,
                Value.Numeric(WindowsImageResult.Empty.X));
            AddImageDefault(BuiltinVariableNames.GImgY,
                Value.Numeric(WindowsImageResult.Empty.Y));
            AddImageDefault(BuiltinVariableNames.AllImgX,
                Value.FromArray(UwscArray.FromValues([])));
            AddImageDefault(BuiltinVariableNames.AllImgY,
                Value.FromArray(UwscArray.FromValues([])));

            void AddImageDefault(string name, Value value)
            {
                if (!program.Globals.Any(declaration =>
                    declaration.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    globals[name] = value;
                }
            }
        }

        private async ValueTask CompleteRunAsync(Exception? executionFailure)
        {
            State.ComEvents?.Close();
            Exception? hotkeyCleanup = null;
            try
            {
                if (hotkeys is not null)
                {
                    await hotkeys.DisposeAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
            }
            catch (Exception failure)
            {
                if (executionFailure is not null)
                {
                    RecordFinallyError(executionFailure, failure);
                }
                else
                {
                    hotkeyCleanup = failure;
                }
            }

            // 最後の文で発生した超過も、通知受付を閉じた後に確定します。
            if (executionFailure is null
                && Volatile.Read(ref hotkeyFailure) is { } pendingHotkeyFailure)
            {
                if (hotkeyCleanup is not null)
                {
                    RecordFinallyError(pendingHotkeyFailure.SourceException, hotkeyCleanup);
                }

                hotkeyCleanup = pendingHotkeyFailure.SourceException;
            }

            try
            {
                await StopWorkersAsync(executionFailure ?? hotkeyCleanup).ConfigureAwait(WorkerThreadContext.IsCurrent);
                finallyCancellation?.Dispose();
            }
            catch (Exception failure) when (executionFailure is not null || hotkeyCleanup is not null)
            {
                RecordFinallyError(executionFailure ?? hotkeyCleanup!, failure);
            }

            if (hotkeyCleanup is not null)
            {
                ExceptionDispatchInfo.Capture(hotkeyCleanup).Throw();
            }
        }

        private async ValueTask<Value> EvaluateAsync(
            Expr expression, Scope scope, int line,
            bool completeDllAssignment = false)
        {
            try
            {
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (AutomationFailureException failure)
            {
                throw ScriptAutomationFailure(line, failure);
            }

            try
            {
                return await (completeDllAssignment && State.Control is not null
                    ? EvaluateNativeDllAssignmentAsync(expression, scope)
                    : expression.EvaluateAsync(scope, token))
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (AutomationFailureException failure)
            {
                throw ScriptAutomationFailure(line, failure);
            }
            catch (Exception ex) when (ScriptErrors.IsScriptError(ex))
            {
                throw new ScriptException(line, ex.Message, ex);
            }
        }

        private static T At<T>(int line, Func<T> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (ScriptErrors.IsScriptError(ex))
            {
                throw new ScriptException(line, ex.Message, ex);
            }
        }

        private void Write(Scope scope, string name,
            Value value, int line, bool declare = false) => At(line, () =>
        {
            scope.Write(name, value, declare);
            return true;
        });

        private async ValueTask<bool> ConditionAsync(
            Expr expression, Scope scope, int line)
        {
            Value value = await EvaluateAsync(EffectiveShortCircuit
                ? new ConditionExpr(expression) : expression,
                scope, line).ConfigureAwait(WorkerThreadContext.IsCurrent);
            return NumberAt(line, value) != 0;
        }

        private static double NumberAt(int line, Value value) =>
            At(line, () => NumericOperations.Number(value));

        private Value ArrayElement(Value value, int line)
        {
            return value;
        }

        private static string ReferenceName(
            Expr expression, Scope? caller = null) => ResolveWithReference(expression, caller) switch
            {
                NameExpr name => name.Name,
                IndexExpr index => index.Name,
                ReferenceExpr reference => ReferenceName(reference.Target, caller),
                MemberExpr { Target: NameExpr owner, Indexed: true } member =>
                    owner.Name + "." + member.Name,
                MemberExpr { Target: NameExpr owner, Arguments.Count: 0 } member =>
                    owner.Name + "." + member.Name,
                _ => throw new InvalidOperationException(
                    "書込み可能な変数または配列参照を指定してください。")
            };

        private async ValueTask WriteReferenceAsync(
            Expr reference, Value value, Scope scope,
            bool deferArrayBoundsError = false)
        {
            reference = NormalizeModuleReference(reference, scope);
            string name = ReferenceName(reference);
            if (reference is IndexExpr index
                && index.Indices.Count > 0)
            {
                if (scope.Read(name).Kind == ValueKind.HashTable)
                {
                    if (index.Indices.Count != 1)
                    {
                        throw new InvalidOperationException("HASHTBLへの書込みは単一キーを指定してください。");
                    }

                    Value[] hashIndices = await index.ResolveValuesAsync(scope, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    lock (sharedVariablesGate)
                    {
                        scope.Write(name, Value.FromHashTable(scope.Read(name).AsHashTable().Set(
                            hashIndices[0].Display(), value)));
                    }
                    return;
                }

                int[] indices = await index.ResolveAsync(
                    scope, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                lock (sharedVariablesGate)
                {
                    Value current = scope.Read(name);
                    UwscArray array = current.AsArray();
                    if (deferArrayBoundsError && indices.Length <= array.Rank
                        && indices.Where((coordinate, dimension) =>
                            coordinate < array.LowerBounds[dimension]
                            || coordinate > array.UpperBounds[dimension]).Any())
                    {
                        // DLLの範囲外エラーを記録し、残る引数の書戻しを続けます。
                        State.NativeDllWriteBack!.CopybackFailure ??=
                            ExceptionDispatchInfo.Capture(new InvalidOperationException(
                                "配列の添字が範囲外です。"));
                        return;
                    }
                    scope.Write(name, current with
                    {
                        Array = array.Set(indices, value, token)
                    });
                }
            }
            else
            {
                scope.Write(name, value);
            }
        }

        private async ValueTask EvaluateWriteAsync(
            Expr reference, Value value, Scope scope, int line)
        {
            try
            {
                await WriteReferenceAsync(reference, value,
                    scope).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception ex) when (ScriptErrors.IsScriptError(ex))
            {
                throw new ScriptException(line, ex.Message, ex);
            }
        }

        internal ValueTask<Value> InvokeAsync(CallExpr call, Scope caller) =>
            InvokeAsync(call, caller, assignment: null);

        private async ValueTask<Value> InvokeAsync(
            CallExpr call, Scope caller, NativeDllAssignment? assignment)
        {
            if (ContinueStoppedNativeDllExpression(call, caller))
            {
                // 実行中の式では、停止済み関数の省略と副作用のない演算を完了します。
                token.ThrowIfCancellationRequested();
                ThrowIfAutomationFailed();
            }
            else
            {
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            if (call.Name.Equals(BuiltinFunctionNames.Sleep,
                StringComparison.OrdinalIgnoreCase))
            {
                return await InvokeSleepAsync(call, caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            if (powerOperation is not null && call.Name.Equals(
                BuiltinFunctionNames.Poff, StringComparison.OrdinalIgnoreCase))
            {
                List<Value> values = await BuiltinInvocation.EvaluateArgumentsAsync(
                    call.Arguments, caller, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                var request = PowerCommandRequest.Read(values);
                token.ThrowIfCancellationRequested();
                Action? completion = powerOperation(request);
                if (request.Command == PowerCommand.RestartApplication)
                {
                    throw new StopSignal(0, completion);
                }

                return Value.Empty;
            }

            if (FileFunctions.IsBuiltin(call.Name))
            {
                return await InvokeFileAsync(call, caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            if (WindowsFunctions.IsBuiltin(call.Name))
            {
                return await InvokeWindowsAsync(call, caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            if (BuiltinFunctions.IsBuiltin(call.Name))
            {
                return await InvokeBuiltinAsync(call,
                    caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            return await InvokeRoutineAsync(call, caller, assignment)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        private async ValueTask<Value> InvokeRoutineAsync(
            CallExpr call, Scope caller, NativeDllAssignment? assignment)
        {
            ModuleState? module = caller.Module;
            if (module is null || !module.Definition.Routines.TryGetValue(
                call.Name, out Routine? routine))
            {
                if (module?.NativeDlls.TryGet(call.Name,
                    out NativeDllDeclarationStatement? member) == true)
                {
                    return await InvokeNativeDllAsync(member, call.Arguments,
                        caller, assignment)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                }

                module = null;
                if (!TryGetGlobalRoutine(call.Name, out routine))
                {
                    if (ActiveNativeDlls.TryGet(call.Name,
                        out NativeDllDeclarationStatement? declaration))
                    {
                        return await InvokeNativeDllAsync(declaration,
                            call.Arguments, caller, assignment)
                            .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }

                    throw new InvalidOperationException(
                        $"未定義の関数: {call.Name}");
                }
            }

            return await InvokeRoutineDefinitionAsync(call, caller,
                routine, module).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        private async ValueTask<Value> InvokeRoutineDefinitionAsync(
            CallExpr call, Scope caller, Routine routine, ModuleState? module)
        {
            if (call.Arguments.Any(argument => argument is NamedExpr))
            {
                throw new InvalidOperationException(
                    "ユーザー関数に名前付き引数は指定できません。");
            }

            int required = routine.Parameters.Count(
                p => p.Default is null);
            if (call.Arguments.Count < required
                || call.Arguments.Count > routine.Parameters.Count)
            {
                throw new InvalidOperationException(
                    $"引数の数が一致しません: {call.Name}");
            }

            for (int i = 0; i < call.Arguments.Count; i++)
            {
                Parameter parameter = routine.Parameters[i];
                if (parameter.Default is null
                    && call.Arguments[i] is OmittedExpr)
                {
                    throw new InvalidOperationException(
                        $"必須引数を省略できません: {parameter.Name}");
                }
            }

            if (State.NativeDllWriteBackDepth > 0 && finallyDepth == 0
                && State.LifetimeToken.IsCancellationRequested)
            {
                // 停止後のDLL書戻しでは、ユーザー関数を実行せずEMPTYを使います。
                token.ThrowIfCancellationRequested();
                return Value.Empty;
            }

            if (caller.CallDepth >= ScriptLimits.MaxCallDepth)
            {
                throw new InvalidOperationException(
                    "関数の呼出し階層が深すぎます。");
            }

            // 実行フレームをさらに積む前に、同期的な再帰を中断します。
            await Task.CompletedTask.ConfigureAwait(
                (WorkerThreadContext.IsCurrent ? ConfigureAwaitOptions.ContinueOnCapturedContext : ConfigureAwaitOptions.None) | ConfigureAwaitOptions.ForceYielding);
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            var callee = new Scope(this)
            {
                CallDepth = caller.CallDepth + 1,
                HasRoutineResult = true,
                RoutineReturnsValue = routine.ReturnsValue,
                Module = module,
                SourcePath = routine.Body.FirstOrDefault()?.SourcePath ?? caller.SourcePath
            };
            callee.Locals[BuiltinVariableNames.Result] = Value.Empty;
            var copybacks = new List<(string Parameter, Expr Target)>();
            await BindRoutineArgumentsAsync(call, routine, caller, callee,
                copybacks).ConfigureAwait(WorkerThreadContext.IsCurrent);

            ExceptionDispatchInfo? copybackFailure = null;
            if (State.NativeDllWriteBackDepth > 0 && finallyDepth == 0)
            {
                copybackFailure = await RunNativeDllRoutineBodyAsync(routine, callee)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            else
            {
                await BlockAsync(routine.Body, callee)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            Value result = copybackFailure is null
                ? callee.Read(BuiltinVariableNames.Result) : Value.Empty;
            foreach ((string? parameter, Expr? target) in copybacks)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await WriteReferenceAsync(target,
                        callee.Read(parameter), caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                catch (Exception error) when (error is ScriptException
                    || ScriptErrors.IsScriptError(error))
                {
                    // 失敗した引数を飛ばし、残るVAR引数の書戻しを続けます。
                    copybackFailure ??= ExceptionDispatchInfo.Capture(error);
                }
            }
            if (copybackFailure is not null)
            {
                if (!State.LifetimeToken.IsCancellationRequested
                    && State.NativeDllRoutine is { } parent
                    && parent.WriteBackDepth == State.NativeDllWriteBackDepth)
                {
                    // 内側のVARエラーは、呼出し元のEXCEPTで処理できるようにします。
                    parent.CopybackFailure = copybackFailure.SourceException;
                    copybackFailure.Throw();
                }
                if (State.NativeDllWriteBack is { } writeBack)
                {
                    // 直接の添字関数のエラーは、DLLの書戻し後に通知します。
                    writeBack.CopybackFailure ??= copybackFailure;
                    if (finallyDepth == 0 && !State.LifetimeToken.IsCancellationRequested)
                    {
                        // 未処理のVARエラー後は、その添字式の評価を打ち切ります。
                        throw new NativeDllIndexAbortSignal(writeBack);
                    }
                }
                else
                {
                    copybackFailure.Throw();
                }
            }

            if (State.NativeDllWriteBackDepth > 0)
            {
                if (State.LifetimeToken.IsCancellationRequested
                    && State.NativeDllRoutine is { } parent
                    && parent.WriteBackDepth == State.NativeDllWriteBackDepth)
                {
                    // 内側の返値を代入した後、呼出し文の境界で外側を停止します。
                    token.ThrowIfCancellationRequested();
                    ThrowIfAutomationFailed();
                }
                else
                {
                    await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
            }
            return routine.ReturnsValue ? result : Value.Empty;
        }

        private async ValueTask<Value> InvokeLogPrintAsync(CallExpr call, Scope caller)
        {
            if (call.Arguments.Count is < 1 or > 5 || call.Arguments[0] is OmittedExpr)
            {
                throw new InvalidOperationException(
                    "LOGPRINTには表示フラグと、省略可能な位置・サイズを指定してください。");
            }

            if (host is not IScriptLogViewHost viewHost || !viewHost.SupportsLogView)
            {
                throw new InvalidOperationException("この出力先はログウィンドウに対応していません。");
            }

            Value flag = await call.Arguments[0].EvaluateAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            bool visible = NumericOperations.RuntimeNumber(flag) != 0;
            int?[] bounds = new int?[4];
            for (int index = 1; index < call.Arguments.Count; index++)
            {
                if (call.Arguments[index] is OmittedExpr)
                {
                    continue;
                }

                Value value = await call.Arguments[index].EvaluateAsync(caller, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                double number = Math.Truncate(NumericOperations.RuntimeNumber(value));
                if (number < int.MinValue || number > int.MaxValue
                    || (index >= 3 && number < 1))
                {
                    throw new InvalidOperationException("LOGPRINTの位置またはサイズが範囲外です。");
                }

                bounds[index - 1] = (int)number;
            }

            var view = new ScriptLogView(visible, bounds[0], bounds[1], bounds[2], bounds[3]);
            await HostCallAsync(async () =>
            {
                await viewHost.SetLogViewAsync(view, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            }).ConfigureAwait(WorkerThreadContext.IsCurrent);
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            return Value.Empty;
        }

        private async ValueTask<Value> InvokeStopFormAsync(CallExpr call, Scope caller)
        {
            if (call.Arguments.Count is < 1 or > 3
                || call.Arguments[0] is OmittedExpr)
            {
                throw new InvalidOperationException(
                    $"{BuiltinFunctionNames.StopForm}には表示フラグと省略可能な2座標を指定してください。");
            }

            if (host is not IScriptStopFormHost target || !target.SupportsStopForm)
            {
                throw new InvalidOperationException("この実行元は停止画面に対応していません。");
            }

            List<Value> arguments = await BuiltinInvocation.EvaluateArgumentsAsync(
                call.Arguments, caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            var request = StopFormRequest.Read(arguments);
            await HostCallAsync(async () =>
            {
                await target.SetStopFormAsync(new(request.Visible, request.X, request.Y), token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }).ConfigureAwait(WorkerThreadContext.IsCurrent);
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            return Value.String("");
        }

        private async ValueTask<Value> InvokeSleepAsync(
            CallExpr call, Scope caller)
        {
            if (call.Arguments.Count != 1 || call.Arguments[0] is OmittedExpr)
            {
                throw new InvalidOperationException(
                    "SLEEPには秒数を一つ指定してください。");
            }

            double seconds = NumericOperations.RuntimeNumber(
                await call.Arguments[0].EvaluateAsync(
                    caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent));
            if (seconds < 0 || seconds > int.MaxValue
                / 1000d)
            {
                throw new InvalidOperationException(
                    "SLEEPの秒数が範囲外です。");
            }

            await HostCallAsync(async () =>
            {
                var duration = TimeSpan.FromSeconds(seconds);
                if (pauseControl is not null && finallyDepth == 0)
                {
                    await pauseControl.DelayAsync(duration, token,
                        host.DelayAsync, () => windows?.CanPause ?? true).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                else
                {
                    await host.DelayAsync(duration, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
            }).ConfigureAwait(WorkerThreadContext.IsCurrent);

            if (seconds == 0)
            {
                // 0秒でも後続文を実行する前に、スケジューラーへ処理を戻します。
                await Task.Yield();
            }

            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            return Value.Empty;
        }

        private async ValueTask<Value> InvokeFileAsync(
            CallExpr call, Scope caller)
        {
            (int required, int maximum) = FileFunctions.GetSignature(call.Name);
            if (call.Arguments.Count < required || call.Arguments.Count > maximum
                || call.Arguments.Take(required).Any(argument => argument is OmittedExpr))
            {
                throw new InvalidOperationException(
                    $"引数の数または省略位置が不正です: {call.Name}");
            }

            List<Value> arguments = await BuiltinInvocation.EvaluateArgumentsAsync(
                call.Arguments, caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);

            ThrowIfStoppedNativeDllRoutine();
            FileFunctions.Validate(call.Name, arguments);
            if (files is null)
            {
                throw new InvalidOperationException(
                    "ファイル操作adapterが設定されていません。");
            }

            return await HostCallAsync(async () =>
            {
                ScriptFileCallResult fileResult = await sharedFiles!.InvokeWithSnapshotAsync(
                    call.Name.ToUpperInvariant(), arguments,
                    token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                if (call.Name.Equals(BuiltinFunctionNames.GetDir,
                    StringComparison.OrdinalIgnoreCase))
                {
                    IReadOnlyList<string> names = fileResult.DirectoryFiles;
                    Value directorySnapshot = StringArray(names);
                    await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                    PublishGlobal(caller, BuiltinVariableNames.GetdirFiles, directorySnapshot);
                }

                return fileResult.Value;
            }).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        private async ValueTask<Value> InvokeWindowsAsync(
            CallExpr call, Scope caller)
        {
            WindowsFunctions.CheckCall(call.Name, call.Arguments);
            List<Value> arguments = await BuiltinInvocation.EvaluateArgumentsAsync(
                call.Arguments, caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);

            bool skipStoppedBuiltin = SkipStoppedNativeDllBuiltin(call.Name);
            if (!skipStoppedBuiltin)
            {
                ThrowIfStoppedNativeDllRoutine();
            }
            WindowsFunctions.Validate(call.Name, arguments, token);
            if (skipStoppedBuiltin)
            {
                token.ThrowIfCancellationRequested();
                return Value.String(string.Empty);
            }
            IWindowsAutomationSession session = windows
                ?? throw MissingWindowsAdapter();
            try
            {
                Value windowResult;
                Exception? operationFailure = null;
                try
                {
                    windowResult = await WaitWithAutomationFailureAsync(() => session.InvokeAsync(
                        call.Name.ToUpperInvariant(), arguments,
                        token)).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                catch (Exception failure)
                {
                    operationFailure = failure;
                    throw;
                }
                finally
                {
                    PublishItemSnapshot(call.Name, session, caller, operationFailure);
                }

                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                await PublishResultSnapshotsAsync(call.Name, session, caller)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                return windowResult;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                and not StopSignal and not AutomationFailureException
                && !IsComCallbackScriptError(ex))
            {
                if (SuppressComError(ex))
                {
                    return call.Name.Equals(BuiltinFunctionNames.GetOleItem,
                        StringComparison.OrdinalIgnoreCase)
                        ? ComErrorValue with
                        {
                            Number = -1,
                            NativeScalar = -1
                        }
                        : ComErrorValue;
                }

                string? message;
                try
                {
                    message = session.GetScriptErrorMessage(call.Name, ex);
                }
                catch (Exception mappingError) when (mappingError is not OperationCanceledException)
                {
                    throw new HostFailure(mappingError);
                }

                if (message is not null)
                {
                    // 式評価の境界で呼出し行を付けてscript例外へ変換します。
                    throw new InvalidOperationException(message, ex);
                }

                throw new HostFailure(ex);
            }
        }

        public async ValueTask<Value> InvokeMemberAsync(Value target, string name,
            IReadOnlyList<Value> arguments, Value? assigned, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfStoppedNativeDllRoutine();
            if (windows is null)
            {
                throw MissingWindowsAdapter();
            }

            return await WindowsObjectCallAsync(async () =>
            {
                Value result = await (windows is IControlledComMemberInvoker controlled
                    && (State.NumericArithmetic as INumericControlArithmetic)
                        ?.ActiveControlWord is ushort controlWord
                    ? controlled.InvokeControlledMemberAsync(target, name, arguments,
                        assigned, controlWord, token)
                    : windows.InvokeMemberAsync(target, name, arguments, assigned, token))
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                return result;
            }, () => ComErrorValue).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        public async ValueTask<Value> InvokeReferenceMemberAsync(MemberExpr member,
            Expr? assignment, Scope caller)
        {
            Value target = await member.Target.EvaluateAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            var arguments = new List<WindowsMemberArgument>();
            foreach (Expr argument in member.Arguments)
            {
                Expr supplied = argument is NamedExpr named ? named.Argument : argument;
                Expr source = supplied is ReferenceExpr reference ? reference.Target : supplied;
                if (supplied is ReferenceExpr && source is NameExpr or IndexExpr)
                {
                    string name = ReferenceName(source, caller);
                    if (name.Equals(BuiltinVariableNames.ComErrFlg,
                        StringComparison.OrdinalIgnoreCase)
                        || IsConstant(caller, name)
                        || (!caller.Locals.ContainsKey(name)
                            && BuiltinConstants.TryGet(name, out _)))
                    {
                        throw new InvalidOperationException("定数をCOMのVAR引数に渡せません。");
                    }

                    if (source is IndexExpr index && caller.Read(name).Kind == ValueKind.HashTable
                        && index.Indices.Count > 1)
                    {
                        throw new InvalidOperationException("HASHTBLのVAR引数は単一キーを指定してください。");
                    }
                }

                arguments.Add(new(await source.EvaluateAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent),
                    supplied is ReferenceExpr)
                {
                    Name = argument is NamedExpr label ? label.Name : null
                });
            }

            Value? assigned = assignment is null ? null
                : await assignment.EvaluateAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            ThrowIfStoppedNativeDllRoutine();
            if (windows is null)
            {
                throw MissingWindowsAdapter();
            }

            bool suppressed = false;
            WindowsMemberResult result = await WindowsObjectCallAsync(async () =>
            {
                WindowsMemberResult result = await (
                    windows is IControlledComMemberInvoker controlled
                    && (State.NumericArithmetic as INumericControlArithmetic)
                        ?.ActiveControlWord is ushort controlWord
                    ? controlled.InvokeControlledMemberWithReferencesAsync(target,
                        member.Name, arguments, assigned, controlWord, token)
                    : windows.InvokeMemberWithReferencesAsync(target, member.Name,
                        arguments, assigned, token))
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                if (result.Arguments.Count != arguments.Count)
                {
                    throw new InvalidOperationException("COMの書戻し引数数が一致しません。");
                }

                return result;
            }, () =>
            {
                suppressed = true;
                return new WindowsMemberResult(ComErrorValue, []);
            }).ConfigureAwait(WorkerThreadContext.IsCurrent);

            if (suppressed)
            {
                // 失敗した呼出しの参照先を再評価せず、未公開の書戻しを捨てます。
                return result.Value;
            }

            await WriteBackComReferencesAsync(member, result, caller)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            return result.Value;
        }

        private async ValueTask WriteBackComReferencesAsync(
            MemberExpr member, WindowsMemberResult result, Scope caller)
        {
            for (int i = 0; i < member.Arguments.Count; i++)
            {
                Expr argument = member.Arguments[i] is NamedExpr named ? named.Argument : member.Arguments[i];
                if (argument is ReferenceExpr reference)
                {
                    await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                    if (reference.Target is MemberExpr property)
                    {
                        await property.InvokeAsync(caller, new LiteralExpr(result.Arguments[i]),
                            token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }
                    else
                    {
                        await WriteReferenceAsync(reference.Target, result.Arguments[i], caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }
                }
            }
        }

        private async ValueTask<T> WindowsObjectCallAsync<T>(
            Func<ValueTask<T>> action, Func<T> onSuppressed,
            int? line = null)
        {
            try
            {
                return await WaitWithAutomationFailureAsync(action)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                and not StopSignal and not AutomationFailureException
                && !IsComCallbackScriptError(ex))
            {
                if (SuppressComError(ex))
                {
                    return onSuppressed();
                }

                string? message;
                try
                {
                    message = windows!.GetObjectScriptErrorMessage(ex);
                }
                catch (Exception mappingError) when (mappingError is not OperationCanceledException)
                {
                    throw new HostFailure(mappingError);
                }

                if (message is not null)
                {
                    if (line is int sourceLine)
                    {
                        throw new ScriptException(sourceLine, message, ex);
                    }

                    throw new InvalidOperationException(message, ex);
                }

                throw new HostFailure(ex);
            }
        }

        private static Value ComErrorValue => Value.Numeric(-999999) with
        {
            NativeVariantType = 3,
            NativeScalar = -999999
        };

        private bool SuppressComError(Exception failure)
        {
            bool isComError;
            try
            {
                isComError = windows!.IsComError(failure);
            }
            catch (Exception classificationError)
                when (classificationError is not OperationCanceledException)
            {
                throw new HostFailure(classificationError);
            }

            if (!isComError)
            {
                return false;
            }

            State.ComErrorOccurred = true;
            return State.IgnoreComErrors;
        }

        private async ValueTask HostCallAsync(Func<ValueTask> action)
        {
            try
            {
                await WaitWithAutomationFailureAsync(action)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                and not StopSignal and not AutomationFailureException
                && !IsComCallbackScriptError(ex))
            {
                throw new HostFailure(ex);
            }
        }

        private async ValueTask<T> HostCallAsync<T>(
            Func<ValueTask<T>> action)
        {
            try
            {
                return await WaitWithAutomationFailureAsync(action)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                and not StopSignal and not AutomationFailureException
                && !IsComCallbackScriptError(ex))
            {
                throw new HostFailure(ex);
            }
        }

        private async ValueTask<T> WaitWithInputPollingAsync<T>(
            ValueTask<T> operation)
        {
            if ((windows?.RequiresInputPolling != true
                && hotkeys is null && State.ComEvents is null)
                || operation.IsCompleted)
            {
                return await operation.ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            Task<T> pending = operation.AsTask();
            await WaitWithInputPollingAsync(pending).ConfigureAwait(WorkerThreadContext.IsCurrent);
            return await pending.ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        private async Task WaitWithInputPollingAsync(Task operation,
            CancellationTokenSource? automationStop = null)
        {
            ExceptionDispatchInfo? pollingFailure = null;
            ExceptionDispatchInfo? callbackFailure = null;
            Exception? automationFailure = null;
            bool cancellationPrecedesFailure = false;
            if (windows?.RequiresInputPolling == true || hotkeys is not null
                || State.ComEvents is not null || automationStop is not null)
            {
                while (!operation.IsCompleted
                    && !token.IsCancellationRequested)
                {
                    if (automationStop is not null)
                    {
                        try
                        {
                            ThrowIfAutomationFailed();
                        }
                        catch (Exception failure)
                        {
                            automationFailure = failure;
                            cancellationPrecedesFailure = token.IsCancellationRequested;
                            try
                            {
                                await automationStop.CancelAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                            }
                            catch (Exception error)
                            {
                                AddAutomationCleanupError(failure, error);
                            }

                            break;
                        }
                    }

                    if (pollingFailure is null)
                    {
                        try
                        {
                            windows?.PollInput(token);
                            await DispatchHotkeyAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                        }
                        catch (Exception error)
                        {
                            pollingFailure = ExceptionDispatchInfo.Capture(error);
                            if (automationStop is null)
                            {
                                break;
                            }
                        }
                    }

                    try
                    {
                        await DispatchComEventAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }
                    catch (Exception failure)
                    {
                        callbackFailure = ExceptionDispatchInfo.Capture(failure);
                        break;
                    }

                    await Task.WhenAny(operation, Task.Delay(
                        WindowsCompletionPollInterval, clock,
                        CancellationToken.None)).ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
            }

            await AwaitPolledOperationAsync(operation, automationStop,
                pollingFailure, callbackFailure, automationFailure,
                cancellationPrecedesFailure)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        private async ValueTask AwaitPolledOperationAsync(Task operation,
            CancellationTokenSource? automationStop,
            ExceptionDispatchInfo? pollingFailure,
            ExceptionDispatchInfo? callbackFailure,
            Exception? automationFailure, bool cancellationPrecedesFailure)
        {
            OperationCanceledException? operationCancellation = null;

            // 採取失敗や取消でも、開始済みの操作を置き去りにしません。
            try
            {
                await operation.ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
            catch (Exception failure) when (callbackFailure is not null)
            {
                if (!ReferenceEquals(failure, callbackFailure.SourceException)
                    && !(failure is OperationCanceledException cancelled
                        && callbackFailure.SourceException is OperationCanceledException callbackCancelled
                        && cancelled.CancellationToken.IsCancellationRequested
                        && cancelled.CancellationToken == callbackCancelled.CancellationToken
                        && cancelled.Data.Count == 0))
                {
                    if (windows?.IsComError(failure) == true)
                    {
                        callbackFailure.SourceException.Data[
                            "UWSCSharp.ComEventOperationFailure"] = failure;
                    }
                    else
                    {
                        RecordFinallyError(callbackFailure.SourceException, failure);
                    }
                }
            }
            catch (Exception error) when (automationFailure is not null)
            {
                operationCancellation = error as OperationCanceledException;
                if (!(cancellationPrecedesFailure && operationCancellation is not null)
                    && (error is not OperationCanceledException cancelled
                        || cancelled.CancellationToken != automationStop!.Token || error.Data.Count != 0))
                {
                    AddAutomationCleanupError(automationFailure, error);
                }
            }
            catch (Exception primary) when (pollingFailure is not null)
            {
                primary.Data["UWSCSharp.InputPollingErrors"] =
                    pollingFailure.SourceException;
                throw;
            }

            callbackFailure?.Throw();
            if (automationFailure is not null)
            {
                if (cancellationPrecedesFailure)
                {
                    OperationCanceledException cancelled = operationCancellation
                        ?? new OperationCanceledException(automationStop!.Token);
                    AddAutomationError(cancelled, automationFailure);
                    if (pollingFailure is not null)
                    {
                        AddAutomationError(cancelled, pollingFailure.SourceException);
                    }

                    throw cancelled;
                }

                if (pollingFailure is not null)
                {
                    AddAutomationCleanupError(pollingFailure.SourceException, automationFailure);
                    pollingFailure.Throw();
                }

                ExceptionDispatchInfo.Capture(automationFailure).Throw();
            }

            pollingFailure?.Throw();
        }

        private static Value StringArray(IReadOnlyList<string> values) =>
            Value.FromArray(UwscArray.Create(
                [values.Count - 1], values.Select(Value.String)));

        private bool IsConstant(Scope caller, string name) =>
            !caller.Locals.ContainsKey(name)
            && ModuleConstant(caller, name);

        private void PublishGlobal(Scope caller, string name, Value value)
        {
            State.Snapshots[name] = value;
            caller.Locals.Remove(name);
        }

        private async ValueTask<Value> InvokeBuiltinAsync(
            CallExpr call, Scope caller)
        {
            StatefulBuiltin kind = StatefulBuiltins.GetKind(call.Name);
            if (TryDispatchStatefulBuiltin(call, caller, kind,
                out ValueTask<Value> dispatched))
            {
                return await dispatched
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            if ((kind == StatefulBuiltin.Random && call.Arguments.Count > 1)
                || (kind == StatefulBuiltin.GetTime && call.Arguments.Count > 2))
            {
                throw new InvalidOperationException(
                    $"引数の数が不正です: {call.Name.ToUpperInvariant()}");
            }

            List<Value> arguments = BuiltinFunctions.ValidatesBeforeEvaluation(call.Name)
                ? await BuiltinInvocation.EvaluateValidatedArgumentsAsync(
                    call, caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent)
                : await BuiltinInvocation.EvaluateArgumentsAsync(
                    call.Arguments, caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);

            if (call.Name.Equals(BuiltinFunctionNames.Length, StringComparison.OrdinalIgnoreCase)
                && arguments.Count == 1 && arguments[0].Kind == ValueKind.Object)
            {
                return await InvokeComLengthAsync(arguments[0])
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
            }

            if (BuiltinFunctions.IsPure(call.Name))
            {
                if (SkipStoppedNativeDllBuiltin(call.Name))
                {
                    token.ThrowIfCancellationRequested();
                    return Value.String(string.Empty);
                }
                return BuiltinFunctions.Evaluate(call.Name,
                    arguments, EffectiveSameString,
                    NumericArithmetic as INumericBuiltinArithmetic);
            }

            switch (kind)
            {
                case StatefulBuiltin.GetTime:
                    return await InvokeGetTimeAsync(arguments, caller)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                case StatefulBuiltin.Random:
                    return await InvokeRandomAsync(call, arguments)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                default:
                    throw new InvalidOperationException(
                        $"未対応の関数: {call.Name}");
            }
        }

        /// <summary>
        /// 引数を評価する前に専用処理へ委譲する組み込み関数を振り分けます。
        /// </summary>
        /// <returns>委譲した場合は<see langword="true"/>。</returns>
        private bool TryDispatchStatefulBuiltin(CallExpr call, Scope caller,
            StatefulBuiltin kind, out ValueTask<Value> dispatched)
        {
            switch (kind)
            {
                case StatefulBuiltin.Speak:
                    dispatched = InvokeSpeakAsync(call, caller);
                    return true;
                case StatefulBuiltin.RecoState:
                case StatefulBuiltin.Dictate:
                    dispatched = InvokeSpeechRecognitionAsync(call, caller, kind);
                    return true;
                case StatefulBuiltin.OleEvent:
                    dispatched = InvokeOleEventAsync(call, caller);
                    return true;
                case StatefulBuiltin.Set8087Cw:
                    dispatched = InvokeNumericControlAsync(call, caller);
                    return true;
                case StatefulBuiltin.SetHotkey:
                    dispatched = InvokeSetHotkeyAsync(call, caller);
                    return true;
                case StatefulBuiltin.StopForm:
                    dispatched = InvokeStopFormAsync(call, caller);
                    return true;
                case StatefulBuiltin.CtrlTrd:
                    dispatched = InvokeCtrlTrdAsync(call, caller);
                    return true;
                case StatefulBuiltin.Eval:
                    dispatched = InvokeEvalAsync(call, caller);
                    return true;
                case StatefulBuiltin.LogPrint:
                    dispatched = InvokeLogPrintAsync(call, caller);
                    return true;
                case StatefulBuiltin.Token:
                    dispatched = InvokeTokenAsync(call, caller);
                    return true;
                case StatefulBuiltin.QSort:
                    dispatched = InvokeQSortAsync(call, caller);
                    return true;
                case StatefulBuiltin.ShiftArray:
                case StatefulBuiltin.SetClear:
                case StatefulBuiltin.Resize:
                    dispatched = MutateArrayAsync(call, caller, kind);
                    return true;
                default:
                    dispatched = default;
                    return false;
            }
        }

        /// <summary>
        /// COMコレクションの要素数をLENGTHの結果として返します。
        /// </summary>
        private async ValueTask<Value> InvokeComLengthAsync(Value collection)
        {
            if (windows is null)
            {
                throw MissingComAdapter();
            }

            return await WindowsObjectCallAsync(async () =>
            {
                IReadOnlyList<Value> items = await windows.EnumerateObjectsAsync(
                    collection, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                return Value.Numeric(items.Count);
            }, () => ComErrorValue).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }

        /// <summary>
        /// GETTIMEの日時を評価し、日時変数を公開して2000年1月1日からの秒数を返します。
        /// </summary>
        private async ValueTask<Value> InvokeGetTimeAsync(
            List<Value> arguments, Scope caller)
        {
            DateTime date = ScriptTime.Evaluate(arguments, clock);
            Dictionary<string, Value> timeVariables = ScriptTime.Variables(date);
            foreach (string name in timeVariables.Keys)
            {
                if (constants.Contains(name))
                {
                    throw new InvalidOperationException(
                        $"定数は変更できません: {name}");
                }
            }

            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            foreach (KeyValuePair<string, Value> pair
                in timeVariables)
            {
                PublishGlobal(caller, pair.Key, pair.Value);
            }

            return Value.Numeric((date.Date
                - new DateTime(2000, 1, 1)).TotalSeconds
                + date.Hour * 3600 + date.Minute * 60 + date.Second);
        }

        /// <summary>
        /// RANDOMの上限を検証し、乱数を返します。
        /// </summary>
        private async ValueTask<Value> InvokeRandomAsync(
            CallExpr call, List<Value> arguments)
        {
            BuiltinFunctions.CheckArguments(
                call.Name, arguments, 0, 1);
            int? bound = arguments.Count == 0 || arguments[0].Kind
                == ValueKind.Omitted ? null
                : UwscRandom.ConvertBound(arguments[0]);
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            return Value.Numeric(random.Next(bound));
        }

        /// <summary>
        /// Windows操作adapterが未設定であることを示す例外を作成します。
        /// </summary>
        private static InvalidOperationException MissingWindowsAdapter() =>
            new("Windows操作adapterが設定されていません。");

        /// <summary>
        /// COM操作adapterが未設定であることを示す例外を作成します。
        /// </summary>
        private static InvalidOperationException MissingComAdapter() =>
            new("COM操作adapterが設定されていません。");

        private static bool StartsDynamicKeyword(string text, string keyword) =>
            text.Length > keyword.Length
            && text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
            && char.IsWhiteSpace(text[keyword.Length]);

        private async ValueTask<Value> InvokeEvalAsync(CallExpr call,
            Scope caller)
        {
            if (call.Arguments.Count != 1
                || call.Arguments[0] is OmittedExpr)
            {
                throw new InvalidOperationException(
                    "EVALには評価する文字列を1個指定してください。");
            }

            if (evaluationDepth >= ScriptLimits.MaxExpressionNesting)
            {
                throw new InvalidOperationException("EVALの入れ子が深すぎます。");
            }

            evaluationDepth++;
            try
            {
                if (evaluationDepth % 16 == 0)
                {
                    // 同期完了するEVALの再帰でも、論理上限より先にスタックを使い切らないようにします。
                    await Task.CompletedTask.ConfigureAwait(
                        (WorkerThreadContext.IsCurrent
                            ? ConfigureAwaitOptions.ContinueOnCapturedContext
                            : ConfigureAwaitOptions.None)
                        | ConfigureAwaitOptions.ForceYielding);
                }

                Value source = await call.Arguments[0].EvaluateAsync(
                    caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                ThrowIfStoppedNativeDllRoutine();
                if (source.Kind != ValueKind.String)
                {
                    throw new InvalidOperationException("EVALには文字列を指定してください。");
                }

                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                string dynamicText = source.Text.Trim();
                if (StartsDynamicKeyword(dynamicText, Keywords.Thread))
                {
                    Expr threadCall = new Expression(dynamicText[Keywords.Thread.Length..],
                        call.PreserveSpecialCharacters, cancellationToken: token)
                        .ParseExpression();
                    await StartThreadAsync(threadCall, caller)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                    return Value.String("");
                }
                if (StartsDynamicKeyword(dynamicText, Keywords.Call))
                {
                    return await InvokeEvalCallAsync(dynamicText, call, caller)
                        .ConfigureAwait(WorkerThreadContext.IsCurrent);
                }
                Expr expression = new Expression(source.Text,
                    call.PreserveSpecialCharacters, allowAssignment: true,
                    cancellationToken: token)
                    .ParseExpression();
                Value result = await expression.EvaluateAsync(caller, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                return result;
            }
            finally
            {
                evaluationDepth--;
            }
        }

        internal async ValueTask<Value> AssignEvaluatedAsync(Expr target,
            Expr expression, Scope caller)
        {
            if (target is MemberExpr member)
            {
                // 通常のmember代入と同じく、対象・引数・右辺の順で評価します。
                await member.InvokeAsync(caller, expression, token)
                    .ConfigureAwait(WorkerThreadContext.IsCurrent);
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                return Value.String("");
            }

            string name = ValidateWritableReference(target, caller);
            if (!Names.CanDeclare(name))
            {
                throw new InvalidOperationException(
                    $"使用できない名前です: {name}");
            }

            Value value = await expression.EvaluateAsync(caller, token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            await WriteReferenceAsync(target, value, caller)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);

            return Value.String("");
        }

        private async ValueTask<Value> InvokeQSortAsync(CallExpr call, Scope caller)
        {
            token.ThrowIfCancellationRequested();
            if (call.Arguments.Count is < 1 or > 10 || call.Arguments[0] is OmittedExpr
                || call.Arguments.Skip(2).Any(argument => argument is OmittedExpr))
            {
                throw new InvalidOperationException(
                    $"引数の数または省略位置が不正です: {BuiltinFunctionNames.QSort}");
            }

            for (int index = 0; index < call.Arguments.Count; index++)
            {
                if (index != 1)
                {
                    ValidateWritableReference(call.Arguments[index], caller);
                }
            }

            List<ResolvedReference> targets = [];
            Value mode = Value.Empty;
            for (int index = 0; index < call.Arguments.Count; index++)
            {
                if (index == 1)
                {
                    if (call.Arguments[index] is not OmittedExpr)
                    {
                        mode = await call.Arguments[index].EvaluateAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    }
                }
                else
                {
                    targets.Add(await ResolveReferenceAsync(call.Arguments[index], caller).ConfigureAwait(WorkerThreadContext.IsCurrent));
                }
            }

            UwscArray[] arrays = [.. targets.Select(target => target.Source.AsArray())];
            IReadOnlyList<UwscArray> sorted = ArrayOperations.Sort(
                arrays, ArrayOperations.SortMode(mode), token);
            // すべての結果と親の形状を準備し、引数の副作用は巻き戻しません。
            Dictionary<string, Value> updates = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < targets.Count; index++)
            {
                ResolvedReference target = targets[index];
                Value parent = updates.TryGetValue(target.Name, out Value pending)
                    ? pending : caller.Read(target.Name);
                updates[target.Name] = PrepareResolvedReplacement(
                    target, target.Source with
                    {
                        Array = sorted[index]
                    }, parent, token);
            }

            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            foreach (KeyValuePair<string, Value> update in updates)
            {
                caller.Write(update.Key, update.Value);
            }

            return Value.Empty;
        }

        private async ValueTask<Value> MutateArrayAsync(
            CallExpr call, Scope caller, StatefulBuiltin kind)
        {
            token.ThrowIfCancellationRequested();
            int minimum = kind == StatefulBuiltin.ShiftArray ? 2 : 1;
            if (call.Arguments.Count < minimum || call.Arguments.Count > 2
                || call.Arguments.Take(minimum).Any(argument => argument is OmittedExpr))
            {
                throw new InvalidOperationException(
                    $"引数の数または省略位置が不正です: {call.Name.ToUpperInvariant()}");
            }

            if (kind == StatefulBuiltin.Resize
                && (call.Arguments.Count == 1 || call.Arguments[1] is OmittedExpr))
            {
                Value queried = await call.Arguments[0].EvaluateAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
                return Value.Numeric(queried.AsArray().UpperBounds[0]);
            }

            ResolvedReference target = await ResolveReferenceAsync(
                call.Arguments[0], caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
            UwscArray array = target.Source.AsArray();
            Value argument = call.Arguments.Count < 2 || call.Arguments[1] is OmittedExpr
                ? Value.Empty
                : await call.Arguments[1].EvaluateAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            UwscArray updated = kind switch
            {
                StatefulBuiltin.SetClear => ArrayOperations.Fill(array, argument, token),
                StatefulBuiltin.ShiftArray => ArrayOperations.Shift(
                    array, ArrayOperations.RangeIndex(argument), token),
                _ => array.Resize(ArrayOperations.ResizeIndex(argument), token)
            };
            await StoreResolvedReferenceAsync(target, target.Source with
            {
                Array = updated
            }, caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
            return kind == StatefulBuiltin.Resize
                ? Value.Numeric(updated.UpperBounds[0]) : Value.Empty;
        }

        private async ValueTask<Value> InvokeTokenAsync(CallExpr call, Scope caller)
        {
            token.ThrowIfCancellationRequested();
            if (call.Arguments.Count is < 2 or > 4
                || call.Arguments.Take(2).Any(argument => argument is OmittedExpr))
            {
                throw new InvalidOperationException(
                    $"引数の数または省略位置が不正です: {BuiltinFunctionNames.Token}");
            }

            ValidateWritableReference(call.Arguments[1], caller);
            Value delimiters = await call.Arguments[0].EvaluateAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            ResolvedReference target = await ResolveReferenceAsync(
                call.Arguments[1], caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
            List<Value> arguments = [delimiters, target.Source];
            for (int index = 2; index < call.Arguments.Count; index++)
            {
                arguments.Add(await call.Arguments[index].EvaluateAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent));
            }

            var args = new CallArguments(arguments);
            (string Token, Value Remainder) split = StringOperations.Token(
                BuiltinText.Convert(args.Get(0)), BuiltinText.Convert(args.Get(1)),
                Math.Truncate(NumericOperations.RuntimeNumber(args.Get(2))) != 0,
                Math.Truncate(NumericOperations.RuntimeNumber(args.Get(3))) != 0);
            await StoreResolvedReferenceAsync(target, split.Remainder, caller).ConfigureAwait(WorkerThreadContext.IsCurrent);
            return Value.String(split.Token);
        }

        private sealed record ResolvedReference(
            string Name, int[] Indices, string? Key, Value Source);

        private string ValidateWritableReference(Expr reference, Scope caller)
        {
            string name = ReferenceName(reference, caller);
            if (IsConstant(caller, name))
            {
                throw new InvalidOperationException($"定数は変更できません: {name}");
            }

            return name;
        }

        private async ValueTask<ResolvedReference> ResolveReferenceAsync(
            Expr reference, Scope caller)
        {
            reference = NormalizeModuleReference(reference, caller);
            string name = ValidateWritableReference(reference, caller);
            // 読取りと書戻しで同じ添字を使い、副作用を一度だけ発生させます。
            int[] indices = [];
            string? key = null;
            Value source;
            if (reference is IndexExpr index && index.Indices.Count > 0)
            {
                if (caller.Read(name).Kind == ValueKind.HashTable)
                {
                    if (index.Indices.Count != 1)
                    {
                        throw new InvalidOperationException("HASHTBLへの書込みは単一キーを指定してください。");
                    }

                    Value[] keys = await index.ResolveValuesAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    key = keys[0].Display();
                    source = caller.Read(name).AsHashTable().Get(key);
                }
                else
                {
                    indices = await index.ResolveAsync(caller, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
                    source = caller.Read(name).AsArray().Get(indices);
                }
            }
            else
            {
                source = caller.Read(name);
            }

            return new ResolvedReference(name, indices, key, source);
        }

        private async ValueTask StoreResolvedReferenceAsync(
            ResolvedReference target, Value replacement, Scope caller)
        {
            replacement = PrepareResolvedReplacement(target, replacement,
                caller.Read(target.Name), token);
            await ObserveCancellationAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            caller.Write(target.Name, replacement);
        }

        private static Value PrepareResolvedReplacement(
            ResolvedReference target, Value replacement, Value parent,
            CancellationToken token)
        {
            if (target.Key is not null)
            {
                return Value.FromHashTable(parent.AsHashTable().Set(target.Key, replacement));
            }

            if (target.Indices.Length > 0)
            {
                return parent with
                {
                    Array = parent.AsArray().Set(target.Indices, replacement, token)
                };
            }

            return replacement;
        }
    }
}
