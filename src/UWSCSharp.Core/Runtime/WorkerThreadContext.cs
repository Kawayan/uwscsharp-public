using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace UWSCSharp.Core;

/// <summary>
/// 非同期処理と捕捉された継続を専用スレッドで実行します。
/// </summary>
/// <remarks>
/// 終了とDisposeはルート処理の完了まで待ちます。
/// 取消を無視する処理は強制終了しません。
/// ConfigureAwait(false)で捕捉を解除した継続は、このスレッドへ戻しません。
/// ルート処理は、その寿命に含める非同期処理をすべて待機してください。
/// </remarks>
internal sealed class WorkerThreadContext : IAsyncDisposable
{
    internal static bool IsCurrent =>
        SynchronizationContext.Current is OwnerSynchronizationContext;

    internal const string CleanupErrorsKey = "UWSCSharp.WorkerCleanupErrors";

    private const string WorkerErrorsKey = "UWSCSharp.WorkerErrors";

    private readonly object cancellationGate = new();
    private readonly BlockingCollection<WorkItem> work = new();
    private readonly ManualResetEventSlim startupDecision = new();
    private readonly TaskCompletionSource ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ended = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationToken token;
    private readonly Func<CancellationToken, Task> operation;
    private readonly Action? initialize;
    private readonly Action? finish;
    private readonly Thread thread;
    private readonly OwnerSynchronizationContext context;
    private Exception? startupFailure;
    private ExceptionDispatchInfo? failure;
    private Task? stopCallbacks;
    private bool closing;

    /// <summary>
    /// 所有スレッドを開始し、その上で初期化とルート処理を行います。
    /// </summary>
    /// <param name="operation">提供されたtokenで協調取消するルート処理です。</param>
    /// <param name="cancellationToken">実行全体の取消を接続します。</param>
    /// <param name="initialize">所有スレッドで処理の開始前に実行します。</param>
    /// <param name="finish">
    /// 初期化を試みた場合に所有スレッドで1回実行します。
    /// 初期化の途中で失敗した状態も扱ってください。
    /// </param>
    /// <param name="startThread">
    /// 起動失敗を検証するための差し替え口です。
    /// 同期的にスレッドを開始するか、例外を返してください。
    /// </param>
    internal WorkerThreadContext(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default,
        Action? initialize = null, Action? finish = null,
        Action<Thread>? startThread = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        this.operation = operation;
        this.initialize = initialize;
        this.finish = finish;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        token = cancellation.Token;
        context = new OwnerSynchronizationContext(this);
        try
        {
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "UWSC script worker"
            };
        }
        catch
        {
            cancellation.Dispose();
            startupDecision.Dispose();
            work.Dispose();
            throw;
        }

        Completion = CompleteAsync();
        try
        {
            if (startThread is null)
            {
                thread.Start();
            }
            else
            {
                startThread(thread);
            }

            if ((thread.ThreadState & ThreadState.Unstarted) != 0)
            {
                throw new InvalidOperationException(
                    "workerの起動処理がスレッドを開始しませんでした。");
            }
        }
        catch (Exception error)
        {
            startupFailure = error;
        }
        finally
        {
            // 起動の成否を確定するまで、所有スレッドは初期化へ進みません。
            startupDecision.Set();
            if ((thread.ThreadState & ThreadState.Unstarted) != 0)
            {
                RecordFailure(startupFailure!);
                ready.TrySetException(startupFailure!);
                ended.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 所有スレッド上の初期化が成功するまで待機します。
    /// </summary>
    internal Task Ready => ready.Task;

    /// <summary>
    /// ルート処理、終了処理、スレッドの終了を待機します。
    /// </summary>
    internal Task Completion
    {
        get;
    }

    /// <summary>
    /// 所有するマネージドスレッドの識別子です。
    /// </summary>
    internal int ManagedThreadId => thread.ManagedThreadId;

    /// <summary>
    /// 所有スレッドがまだ動作しているかを取得します。
    /// </summary>
    internal bool IsAlive => thread.IsAlive;

    /// <summary>
    /// この所有Threadの協調取消tokenです。
    /// </summary>
    internal CancellationToken Token => token;

    /// <summary>
    /// 協調取消callbackを要求し、その完了を返します。
    /// </summary>
    internal Task RequestCancellationAsync() => RequestStop();

    private void Run()
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        bool initializationAttempted = false;
        try
        {
            startupDecision.Wait();
            if (startupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(startupFailure).Throw();
            }

            token.ThrowIfCancellationRequested();
            SynchronizationContext.SetSynchronizationContext(context);
            initializationAttempted = true;
            initialize?.Invoke();
            SynchronizationContext.SetSynchronizationContext(context);
            token.ThrowIfCancellationRequested();
            ready.TrySetResult();
            Task root = operation(token) ?? throw new InvalidOperationException(
                "workerのルート処理がTaskを返しませんでした。");
            _ = root.ContinueWith(_ => Wake(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            while (!root.IsCompleted)
            {
                WorkItem item = work.Take();
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    item.Callback(item.State);
                    item.Completion?.TrySetResult();
                }
                catch (Exception error)
                {
                    if (item.Completion is not null)
                    {
                        item.Completion.TrySetException(error);
                    }
                    else
                    {
                        RecordFailure(error);
                        _ = RequestStop();
                    }
                }
            }

            root.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            RecordFailure(error);
            if (error is OperationCanceledException canceled)
            {
                ready.TrySetCanceled(canceled.CancellationToken);
            }
            else
            {
                ready.TrySetException(error);
            }
        }
        finally
        {
            work.CompleteAdding();
            while (work.TryTake(out WorkItem? remaining))
            {
                remaining.Completion?.TrySetException(
                    new ObjectDisposedException(nameof(WorkerThreadContext)));
            }

            try
            {
                if (initializationAttempted)
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                    finish?.Invoke();
                }
            }
            catch (Exception error)
            {
                RecordFailure(error, CleanupErrorsKey);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                ended.TrySetResult();
            }
        }
    }

    private void Enqueue(WorkItem item)
    {
        try
        {
            work.Add(item);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(WorkerThreadContext));
        }
    }

    private void Wake()
    {
        try
        {
            Enqueue(new WorkItem(_ => { }, null, null));
        }
        catch (ObjectDisposedException)
        {
            // 同期的に終了したルート処理では、既にキューを閉じている場合があります。
        }
    }

    private Task RequestStop()
    {
        lock (cancellationGate)
        {
            if (closing)
            {
                return Task.CompletedTask;
            }

            // 利用側の取消callbackをこのロック内で同期実行しません。
            stopCallbacks ??= cancellation.CancelAsync();
            return stopCallbacks;
        }
    }

    private void RecordFailure(Exception error,
        string key = WorkerErrorsKey)
    {
        if (failure is null)
        {
            failure = ExceptionDispatchInfo.Capture(error);
            return;
        }

        Exception primary = failure.SourceException;
        IEnumerable<Exception> previous = primary.Data[key]
            is AggregateException errors ? errors.InnerExceptions : [];
        primary.Data[key] = new AggregateException(previous.Append(error));
    }

    private async Task CompleteAsync()
    {
        await ended.Task.ConfigureAwait(false);
        if ((thread.ThreadState & ThreadState.Unstarted) == 0)
        {
            // 完了通知の直後に所有スレッドがまだ復帰中でも、終了まで待ちます。
            thread.Join();
        }

        Task? callbacks;
        lock (cancellationGate)
        {
            closing = true;
            callbacks = stopCallbacks;
        }

        if (callbacks is not null)
        {
            try
            {
                await callbacks.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                RecordFailure(error, CleanupErrorsKey);
            }
        }

        cancellation.Dispose();
        startupDecision.Dispose();
        work.Dispose();
        failure?.Throw();
    }

    /// <summary>
    /// 協調取消を要求し、ルート処理と所有スレッドの終了を待ちます。
    /// </summary>
    /// <remarks>
    /// この部品の取消だけで終了した場合、解放自体は成功します。
    /// ルート処理や終了処理の他の失敗は呼出元へ伝えます。
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _ = RequestStop();
        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (
            error.CancellationToken == token
            && token.IsCancellationRequested
            && !error.Data.Contains("UWSCSharp.FinallyErrors")
            && !error.Data.Contains(CleanupErrorsKey))
        {
            // 取消そのものの結果はCompletionにも保持します。
        }
    }

    private sealed record WorkItem(SendOrPostCallback Callback,
        object? State, TaskCompletionSource? Completion);

    private sealed class OwnerSynchronizationContext(
        WorkerThreadContext owner) : SynchronizationContext
    {
        /// <summary>
        /// 同じ所有スレッドへ継続を戻すcontextを返します。
        /// </summary>
        public override SynchronizationContext CreateCopy() => this;

        /// <summary>
        /// 所有スレッドで実行する処理をキューへ追加します。
        /// </summary>
        public override void Post(SendOrPostCallback callback,
            object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            owner.Enqueue(new WorkItem(callback, state, null));
        }

        /// <summary>
        /// 所有スレッドで処理し、その成否を送信元へ返します。
        /// </summary>
        public override void Send(SendOrPostCallback callback,
            object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (Thread.CurrentThread == owner.thread)
            {
                callback(state);
                return;
            }

            var completed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            owner.Enqueue(new WorkItem(callback, state, completed));
            completed.Task.GetAwaiter().GetResult();
        }
    }
}
