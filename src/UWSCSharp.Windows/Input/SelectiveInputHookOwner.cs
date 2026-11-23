using System.Runtime.ExceptionServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 専用スレッドで通知を処理し、解除を確認してから終了します。
/// </summary>
internal sealed class SelectiveInputHookOwner : IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private sealed record StopResult(bool Stopped, Exception? Error);

    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Queue<TaskCompletionSource> refreshes = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource cleaned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly TimeProvider clock;
    private readonly List<Exception> failures = [];
    private Exception? failure;
    private TaskCompletionSource<StopResult>? stopRequest;
    private Task? stopAttempt;
    private Task? stopOperation;
    private long stopRequestedAt;
    private long stopGeneration;
    private long? activeSessionStop;
    private bool closing;
    private bool finished;
    private bool eventDisposed;

    internal SelectiveInputHookOwner(Func<ISelectiveInputHookSession> create,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        this.clock = clock ?? TimeProvider.System;
        thread = new Thread(() => Run(create))
        {
            IsBackground = true,
            Name = "UWSC selective input owner"
        };
        thread.Start();
    }

    internal Task Ready => ready.Task;

    internal Task CleanupCompleted => cleaned.Task;

    internal Exception? Failure => Volatile.Read(ref failure);

    internal bool IsAlive => thread.IsAlive;

    internal bool IsCleanupComplete
    {
        get
        {
            lock (gate)
            {
                return eventDisposed && !thread.IsAlive;
            }
        }
    }

    internal (long Generation, long StartedAt) StopContext
    {
        get
        {
            lock (gate)
            {
                return (stopGeneration, stopRequestedAt);
            }
        }
    }

    internal long BeginSessionStop(long startedAt)
    {
        lock (gate)
        {
            activeSessionStop ??= closing ? stopRequestedAt : startedAt;
            return activeSessionStop.Value;
        }
    }

    internal long? EndSessionStop()
    {
        lock (gate)
        {
            activeSessionStop = null;
            return closing ? stopGeneration : null;
        }
    }

    internal (Exception? Error, int Count) ReadFailure()
    {
        lock (gate)
        {
            return (failure, failures.Count);
        }
    }

    internal bool IsClosing
    {
        get
        {
            lock (gate)
            {
                return closing || finished;
            }
        }
    }

    internal void Wake()
    {
        lock (gate)
        {
            if (!eventDisposed)
            {
                wake.Set();
            }
        }
    }

    internal Task RefreshTargetsAsync()
    {
        lock (gate)
        {
            if (Thread.CurrentThread == thread)
            {
                return Task.FromException(new InvalidOperationException("所有スレッドへ再入できません。"));
            }

            if (closing || finished || Failure is not null)
            {
                return Task.FromException(Failure ?? new ObjectDisposedException(GetType().Name));
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            refreshes.Enqueue(completion);
            wake.Set();
            return completion.Task;
        }
    }

    /// <summary>
    /// 停止を要求し、最大5秒待ちます。残る登録は1回再試行し、未回収の資源を保持します。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Thread.CurrentThread == thread)
        {
            throw new InvalidOperationException("所有スレッド自身の終了を待機できません。");
        }

        lock (gate)
        {
            if (stopAttempt is not null && !stopAttempt.IsCompleted)
            {
                return new ValueTask(stopAttempt);
            }

            if (stopOperation is not null && !stopOperation.IsCompleted)
            {
                stopAttempt = WaitForStopAsync(stopOperation, clock.GetTimestamp());
                return new ValueTask(stopAttempt);
            }

            closing = true;
            stopRequestedAt = activeSessionStop ?? clock.GetTimestamp();
            stopGeneration++;
            while (refreshes.TryDequeue(out TaskCompletionSource? request))
            {
                request.TrySetException(new ObjectDisposedException(GetType().Name));
            }

            if (finished)
            {
                stopOperation = FinishStopAsync(Task.FromResult(new StopResult(true, Failure)));
            }
            else
            {
                stopRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
                stopOperation = FinishStopAsync(stopRequest.Task);
                wake.Set();
            }

            stopAttempt = WaitForStopAsync(stopOperation, stopRequestedAt);
            return new ValueTask(stopAttempt);
        }
    }

    private async Task WaitForStopAsync(Task operation, long startedAt)
    {
        // 待機期限を過ぎても、進行中の解除と所有資源を保持します。
        try
        {
            TimeSpan remaining = StopTimeout - clock.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("入力フックの停止待機が5秒を超えました。");
            }

            await operation.WaitAsync(remaining, clock).ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            RecordFailure(error);
            throw;
        }
    }

    private async Task FinishStopAsync(Task<StopResult> attempt)
    {
        StopResult result = await attempt.ConfigureAwait(false);
        if (result.Stopped)
        {
            await ended.Task.ConfigureAwait(false);
            thread.Join();
            lock (gate)
            {
                if (!eventDisposed)
                {
                    eventDisposed = true;
                    wake.Dispose();
                    cleaned.TrySetResult();
                }
            }
        }

        Exception? error = result.Stopped ? Failure : result.Error;
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private void Run(Func<ISelectiveInputHookSession> create)
    {
        ISelectiveInputHookSession? session = null;
        try
        {
            session = create();
            long refreshedAt = clock.GetTimestamp();
            bool stopAutomatically = false;
            bool pumpFailed = false;
            bool waitFailed = false;
            long startRequestedAt = clock.GetTimestamp();
            long? failedStartAt = null;
            try
            {
                bool start;
                lock (gate)
                {
                    // 開始と停止受付の順序を決め、先に受け付けた停止を優先します。
                    start = !closing;
                }

                if (start)
                {
                    startRequestedAt = BeginSessionStop(startRequestedAt);
                    session.Start();
                    EndSessionStop();

                    ready.TrySetResult();
                }
                else
                {
                    ready.TrySetCanceled();
                }
            }
            catch (Exception error)
            {
                RecordFailure(error);
                ready.TrySetException(error);
                failedStartAt = startRequestedAt;
                stopAutomatically = true;
            }

            while (true)
            {
                TaskCompletionSource<StopResult>? stop;
                TaskCompletionSource? refresh;
                bool refreshDue;
                lock (gate)
                {
                    stop = stopRequest;
                    stopRequest = null;
                    refresh = !closing && Failure is null && refreshes.Count != 0
                        ? refreshes.Dequeue() : null;
                    // 停止受付より後に、新しい更新を開始しません。
                    refreshDue = !closing && Failure is null
                        && (refresh is not null
                            || clock.GetElapsedTime(refreshedAt) >= RefreshInterval);
                }

                if (stopAutomatically || stop is not null)
                {
                    stopAutomatically = false;
                    long startedAt = stop is null
                        ? failedStartAt ?? clock.GetTimestamp() : stopRequestedAt;
                    failedStartAt = null;
                    startedAt = BeginSessionStop(startedAt);
                    SelectiveInputStopResult result;
                    try
                    {
                        result = SelectiveInputHookStop.Run(session, clock, startedAt);
                    }
                    finally
                    {
                        lock (gate)
                        {
                            // 自動回収中の終了要求も、同じ操作の結果を受け取ります。
                            activeSessionStop = null;
                            if (stop is null && stopRequest is not null)
                            {
                                stop = stopRequest;
                                stopRequest = null;
                            }
                        }
                    }
                    Exception? stopError = result.Error;
                    Exception? endError = stopError is null ? Failure : RecordFailure(stopError);
                    stop?.TrySetResult(new StopResult(result.Stopped, endError));
                    if (result.Stopped)
                    {
                        break;
                    }
                }

                if (refreshDue)
                {
                    try
                    {
                        session.RefreshTargets();
                        // 遅れた期限を積み上げず、更新完了から次の期限を決めます。
                        refreshedAt = clock.GetTimestamp();
                        refresh?.TrySetResult();
                    }
                    catch (Exception error)
                    {
                        refresh?.TrySetException(error);
                        RecordFailure(error);
                        stopAutomatically = true;
                        continue;
                    }
                }

                bool pumping = true;
                try
                {
                    lock (gate)
                    {
                        // 更新中に受け付けた停止は、次の通知処理より先に実行します。
                        if (stopRequest is not null)
                        {
                            continue;
                        }
                    }

                    if (!session.Pump())
                    {
                        int milliseconds;
                        lock (gate)
                        {
                            if (refreshes.Count != 0 || stopRequest is not null)
                            {
                                continue;
                            }

                            milliseconds = closing || Failure is not null
                                ? Timeout.Infinite
                                : (int)Math.Clamp(Math.Ceiling(
                                    (RefreshInterval - clock.GetElapsedTime(refreshedAt))
                                        .TotalMilliseconds), 0, 100);
                        }

                        pumping = false;
                        session.Wait(wake, milliseconds);
                    }
                }
                catch (Exception error)
                {
                    bool first;
                    lock (gate)
                    {
                        first = Failure is null;
                        // 停止期限との競合でも各通知処理の最初の障害を失わず、反復分は蓄積しません。
                        if (pumping ? !pumpFailed : !waitFailed)
                        {
                            RecordFailure(error);
                            pumpFailed |= pumping;
                            waitFailed |= !pumping;
                        }
                    }

                    if (first)
                    {
                        stopAutomatically = true;
                    }
                    else
                    {
                        // 通知処理自体が繰り返し失敗しても、停止要求を受け取れるようにします。
                        wake.WaitOne(50);
                    }
                }
            }
        }
        catch (Exception error)
        {
            RecordFailure(error);
        }
        finally
        {
            lock (gate)
            {
                finished = true;
                closing = true;
                Exception error = Failure ?? new ObjectDisposedException(GetType().Name);
                ready.TrySetException(error);
                while (refreshes.TryDequeue(out TaskCompletionSource? refresh))
                {
                    refresh.TrySetException(error);
                }

                stopRequest?.TrySetResult(new StopResult(true, Failure));
                stopRequest = null;
            }

            ended.TrySetResult();
        }
    }

    private Exception RecordFailure(Exception error)
    {
        lock (gate)
        {
            if (!failures.Any(previous => ReferenceEquals(previous, error)))
            {
                failures.Add(error);
            }

            Exception saved = failures.Count == 1 ? failures[0] : new AggregateException(failures);
            Volatile.Write(ref failure, saved);
            while (refreshes.TryDequeue(out TaskCompletionSource? refresh))
            {
                refresh.TrySetException(error);
            }

            return saved;
        }
    }
}
