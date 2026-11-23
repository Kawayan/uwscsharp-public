using System.Runtime.ExceptionServices;

namespace UWSCSharp.Core;

/// <summary>
/// 専用Threadの協調停止、再開、取消、優先度を管理します。
/// </summary>
/// <remarks>
/// 停止はCheckpointAsyncの文境界でだけ作用します。
/// FINALLYを停止から除外する場合は、実行器側でcheckpointを省略してください。
/// DisposeとCompletionはルート処理とThreadのJoinまで待ちます。
/// 取消を無視するnative呼出しは強制終了しません。
/// </remarks>
internal sealed class WorkerThreadControl(
    IWorkerThreadPriority? priority = null) : IAsyncDisposable
{
    private const string ControlErrorsKey = "UWSCSharp.WorkerControlErrors";
    private readonly object gate = new();
    private WorkerThreadContext? worker;
    private Task? completion;
    private TaskCompletionSource? resume;
    private WorkerThreadPriority? pendingPriority;
    private Exception? commandFailure;
    private CancellationToken workerToken;
    private bool started;
    private bool finished;
    private bool initialized;
    private bool priorityDisposed;
    private bool quitRequested;

    /// <summary>
    /// 初期化が完了し、ルート処理を開始できるまで待ちます。
    /// </summary>
    internal Task Ready
    {
        get
        {
            lock (gate)
            {
                return worker?.Ready ?? throw new InvalidOperationException(
                    "workerを開始してから初期化を待機してください。");
            }
        }
    }

    /// <summary>
    /// ルート処理、終了処理、所有ThreadのJoinを待ちます。
    /// </summary>
    internal Task Completion
    {
        get
        {
            lock (gate)
            {
                return completion ?? throw new InvalidOperationException(
                    "workerを開始してから終了を待機してください。");
            }
        }
    }

    /// <summary>
    /// 所有Threadの識別子を取得します。
    /// </summary>
    internal int ManagedThreadId
    {
        get
        {
            lock (gate)
            {
                return worker?.ManagedThreadId ?? 0;
            }
        }
    }

    /// <summary>
    /// 次の文境界で停止する状態かを取得します。
    /// </summary>
    internal bool IsSuspended
    {
        get
        {
            lock (gate)
            {
                return resume is not null;
            }
        }
    }

    /// <summary>
    /// 所有Threadを一度だけ開始します。
    /// </summary>
    internal void Start(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default,
        Action? initialize = null, Action? finish = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (gate)
        {
            if (started || finished)
            {
                throw new InvalidOperationException("workerは開始済みか解放済みです。");
            }

            started = true;
            try
            {
                worker = new WorkerThreadContext(async token =>
                {
                    using CancellationTokenRegistration registration =
                        token.UnsafeRegister(_ => ReleasePause(), null);
                    await operation(token);
                }, cancellationToken, initialize: () =>
                {
                    InitializePriority();
                    initialize?.Invoke();
                }, finish: () => Finish(finish));
                workerToken = worker.Token;
                completion = ObserveCompletionAsync(worker);
            }
            catch (Exception error)
            {
                finished = true;
                Exception? failure = error;
                DisposePriority(ref failure);
                ExceptionDispatchInfo.Capture(failure!).Throw();
            }
        }
    }

    internal bool IsQuitRequested
    {
        get
        {
            lock (gate)
            {
                return quitRequested && commandFailure is null;
            }
        }
    }

    /// <summary>
    /// 文境界で停止を待機し、協調取消を通知します。
    /// </summary>
    internal async ValueTask CheckpointAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? wait;
            lock (gate)
            {
                // 取消による一時停止の解除と同じロック内で確認します。
                workerToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                if (quitRequested)
                {
                    throw new OperationCanceledException(workerToken);
                }

                wait = resume?.Task;
            }

            if (wait is null)
            {
                return;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 対応する制御だけを適用し、未対応値と終了済みは無視します。
    /// </summary>
    /// <remarks>戻り値は内部の適用可否であり、CTRLTRDのscript返値ではありません。</remarks>
    internal bool Apply(int mode)
    {
        Exception? error = null;
        lock (gate)
        {
            if (!started || finished)
            {
                return false;
            }

            switch ((WorkerThreadCommand)mode)
            {
                case WorkerThreadCommand.Suspend:
                    if (!quitRequested && !workerToken.IsCancellationRequested)
                    {
                        resume ??= new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    return true;
                case WorkerThreadCommand.Resume:
                    ReleasePauseLocked();
                    return true;
                case WorkerThreadCommand.Quit:
                    quitRequested = true;
                    // checkpointを解除する前にtokenの取消状態を確定します。
                    _ = worker!.RequestCancellationAsync();
                    ReleasePauseLocked();
                    break;
                case WorkerThreadCommand.Idle:
                case WorkerThreadCommand.Lowest:
                case WorkerThreadCommand.Lower:
                case WorkerThreadCommand.Normal:
                case WorkerThreadCommand.Higher:
                case WorkerThreadCommand.Highest:
                    WorkerThreadPriority value = mode switch
                    {
                        10 => WorkerThreadPriority.Idle,
                        11 => WorkerThreadPriority.Lowest,
                        12 => WorkerThreadPriority.Lower,
                        13 => WorkerThreadPriority.Normal,
                        14 => WorkerThreadPriority.Higher,
                        _ => WorkerThreadPriority.Highest
                    };
                    if (quitRequested)
                    {
                        return false;
                    }

                    try
                    {
                        if (initialized)
                        {
                            priority?.SetPriority(value);
                        }
                        else
                        {
                            pendingPriority = value;
                        }
                    }
                    catch (Exception failure)
                    {
                        error = failure;
                        AddFailure(ref commandFailure, failure, ControlErrorsKey);
                        quitRequested = true;
                        _ = worker!.RequestCancellationAsync();
                        ReleasePauseLocked();
                    }

                    break;
                default:
                    return false;
            }
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return true;
    }

    private void InitializePriority()
    {
        lock (gate)
        {
            priority?.Initialize();
            initialized = true;
            if (pendingPriority is WorkerThreadPriority value)
            {
                priority?.SetPriority(value);
                pendingPriority = null;
            }
        }
    }

    private void Finish(Action? finish)
    {
        lock (gate)
        {
            finished = true;
            ReleasePauseLocked();
        }

        Exception? error = null;
        try
        {
            finish?.Invoke();
        }
        catch (Exception failure)
        {
            error = failure;
        }

        lock (gate)
        {
            DisposePriority(ref error);
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private async Task ObserveCompletionAsync(WorkerThreadContext context)
    {
        Exception? error = null;
        try
        {
            await context.Completion.ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            error = failure;
        }

        lock (gate)
        {
            finished = true;
            ReleasePauseLocked();
            DisposePriority(ref error);
            if (commandFailure is not null)
            {
                if (error is OperationCanceledException canceled
                    && canceled.CancellationToken == workerToken
                    && workerToken.IsCancellationRequested)
                {
                    AddFailure(ref commandFailure, error, ControlErrorsKey);
                    error = commandFailure;
                }
                else
                {
                    AddFailure(ref error, commandFailure, ControlErrorsKey);
                }
            }
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private void DisposePriority(ref Exception? error)
    {
        if (priorityDisposed)
        {
            return;
        }

        priorityDisposed = true;
        try
        {
            priority?.Dispose();
        }
        catch (Exception failure)
        {
            AddFailure(ref error, failure, WorkerThreadContext.CleanupErrorsKey);
        }
    }

    private static void AddFailure(ref Exception? primary,
        Exception error, string key)
    {
        if (primary is null)
        {
            primary = error;
            return;
        }

        if (ReferenceEquals(primary, error))
        {
            return;
        }

        IEnumerable<Exception> previous = primary.Data[key]
            is AggregateException aggregate ? aggregate.InnerExceptions : [];
        primary.Data[key] = new AggregateException(previous.Append(error));
    }

    private void ReleasePause()
    {
        lock (gate)
        {
            ReleasePauseLocked();
        }
    }

    private void ReleasePauseLocked()
    {
        TaskCompletionSource? pending = resume;
        resume = null;
        pending?.TrySetResult();
    }

    /// <summary>
    /// 停止を解除して協調取消を要求し、Joinまで待ちます。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (gate)
        {
            if (!started)
            {
                finished = true;
                Exception? error = null;
                DisposePriority(ref error);
                if (error is not null)
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }

                return;
            }

            pending = completion;
        }

        Apply((int)WorkerThreadCommand.Quit);
        if (pending is null)
        {
            return;
        }

        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (
            error.CancellationToken == workerToken
            && workerToken.IsCancellationRequested
            && !error.Data.Contains(WorkerThreadContext.CleanupErrorsKey)
            && !error.Data.Contains("UWSCSharp.FinallyErrors")
            && !error.Data.Contains(ControlErrorsKey))
        {
            // 取消結果はCompletionへ残し、通常の解放だけは成功とします。
        }
    }
}
