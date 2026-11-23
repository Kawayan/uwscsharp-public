using System.Runtime.ExceptionServices;

namespace UWSCSharp.Windows;

/// <summary>
/// 1つの所有スレッドで設定変更と明示解除を処理します。
/// </summary>
internal sealed class SelectiveInputRunSession(
    Func<SelectiveInputTarget, SelectiveInputDevices, ISelectiveInputHookSession> create,
    TimeProvider clock, SelectiveInputEmergencyStop? emergency = null) : ISelectiveInputHookSession
{
    private sealed class Change(SelectiveInputTarget? target,
        SelectiveInputDevices devices, CancellationToken token, long requestedAt, long releaseVersion)
    {
        internal readonly SelectiveInputTarget? Target = target;
        internal readonly SelectiveInputDevices Devices = devices;
        internal readonly CancellationToken Token = token;
        internal readonly long RequestedAt = requestedAt;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly long ReleaseVersion = releaseVersion;
        internal bool Abandoned;
    }

    private readonly object gate = new();
    private readonly Queue<Change> changes = new();
    private readonly Queue<Exception> pendingFailures = new();
    private SelectiveInputHookOwner? owner;
    private ISelectiveInputHookSession? current;
    private SelectiveInputTarget? target;
    private SelectiveInputDevices devices;
    private Exception? cleanupFailure;
    private bool stopping;
    private long releaseVersion;
    private long sharedStopGeneration = -1;
    private SelectiveInputStopResult? sharedStop;

    public bool IsRegistered => current?.IsRegistered == true;

    internal void Attach(SelectiveInputHookOwner value) => owner = value;

    internal Task ChangeAsync(SelectiveInputTarget? next,
        SelectiveInputDevices selection, CancellationToken token)
    {
        if (next is not null && (selection == SelectiveInputDevices.None
            || (selection & ~SelectiveInputDevices.All) != 0))
        {
            return Task.FromException(new ArgumentOutOfRangeException(nameof(selection)));
        }

        token.ThrowIfCancellationRequested();
        Change change;
        lock (gate)
        {
            if (next is not null)
            {
                emergency?.ThrowIfRequested();
            }

            ObjectDisposedException.ThrowIf(stopping || owner!.IsClosing, this);
            if (next is null)
            {
                releaseVersion++;
            }

            change = new Change(next, selection, token, clock.GetTimestamp(), releaseVersion);
            changes.Enqueue(change);
            if (next is not null)
            {
                emergency?.SetActive(true);
            }
        }

        owner!.Wake();
        return WaitForChangeAsync(change);
    }

    private async Task WaitForChangeAsync(Change change)
    {
        Task first = await Task.WhenAny(change.Completion.Task, change.Stopping.Task).ConfigureAwait(false);
        if (first == change.Stopping.Task)
        {
            try
            {
                TimeSpan remaining = SelectiveInputHookStop.Timeout - clock.GetElapsedTime(change.RequestedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("入力フックの設定変更時の解除待機が5秒を超えました。");
                }

                await change.Completion.Task.WaitAsync(remaining, clock).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                lock (gate)
                {
                    if (!change.Completion.Task.IsCompleted)
                    {
                        change.Abandoned = true;
                        owner!.Wake();
                        throw;
                    }
                }

                // 完了との競合では、処理が返した元の例外を受け取ります。
                await change.Completion.Task.ConfigureAwait(false);
                return;
            }
        }

        await change.Completion.Task.ConfigureAwait(false);
    }

    internal Exception? TakeFailure()
    {
        lock (gate)
        {
            Exception? error = null;
            while (pendingFailures.TryDequeue(out Exception? item))
            {
                error = SelectiveInputHookStop.Combine(error, item);
            }

            return error;
        }
    }

    public void Start()
    {
    }

    public bool Pump()
    {
        Change? change;
        lock (gate)
        {
            change = changes.TryDequeue(out Change? item) ? item : null;
        }

        if (change is not null)
        {
            Process(change);
            if (!owner!.IsClosing)
            {
                // 連続する設定要求の間にも、最大1バッチの入力通知を処理します。
                current?.Pump();
            }

            return true;
        }

        return current?.Pump() ?? false;
    }

    private void Process(Change change)
    {
        try
        {
            CheckChange(change);
            change.Target?.Validate();
            if (change.Target is not null && cleanupFailure is not null)
            {
                var error = new InvalidOperationException(
                    "未解除の入力フックを明示解除してください。", cleanupFailure);
                if (SelectiveInputOperationErrors.IsExpected(cleanupFailure))
                {
                    WindowsOperationErrors.Record(error);
                }
                throw error;
            }

            if (change.Target is not null && target?.Matches(change.Target) == true
                && devices == change.Devices)
            {
                Complete(change, null);
                return;
            }

            if (current is not null)
            {
                change.Stopping.TrySetResult();
                SelectiveInputStopResult result = StopCurrent(change.RequestedAt);

                if (result.Error is not null)
                {
                    ExceptionDispatchInfo.Capture(result.Error).Throw();
                }
            }

            CheckChange(change);
            if (change.Target is not null)
            {
                change.Target.Validate();
                current = create(change.Target, change.Devices);
                owner!.BeginSessionStop(change.RequestedAt);
                try
                {
                    if (emergency is not null)
                    {
                        current.ConfigureEmergency(emergency);
                    }

                    current.Start();
                    lock (gate)
                    {
                        // 待機期限による放棄と、登録の成功確定を同時に決めます。
                        CheckChange(change);
                        target = change.Target;
                        devices = change.Devices;
                        Complete(change, null);
                    }
                }
                catch (Exception error)
                {
                    change.Stopping.TrySetResult();
                    SelectiveInputStopResult result = StopCurrent(change.RequestedAt);

                    throw SelectiveInputHookStop.Combine(error, result.Error)!;
                }
                finally
                {
                    owner.EndSessionStop();
                }
            }

            Complete(change, null);
        }
        catch (Exception error)
        {
            Complete(change, error);
        }
    }

    private void Complete(Change change, Exception? error)
    {
        lock (gate)
        {
            emergency?.SetActive(current is not null
                || changes.Any(pending => pending.Target is not null));
            if (error is null)
            {
                change.Completion.TrySetResult();
            }
            else
            {
                change.Completion.TrySetException(error);
                if (change.Abandoned)
                {
                    pendingFailures.Enqueue(error);
                    _ = change.Completion.Task.Exception;
                }
            }
        }
    }

    private SelectiveInputStopResult StopCurrent(long startedAt)
    {
        long budgetStart = owner!.BeginSessionStop(startedAt);
        SelectiveInputStopResult result;
        long? generation;
        try
        {
            result = current is null
                ? new(true, null) : SelectiveInputHookStop.Run(current, clock, budgetStart);
        }
        finally
        {
            generation = owner.EndSessionStop();
        }
        cleanupFailure = result.Stopped ? null : result.Error;
        if (result.Stopped)
        {
            current = null;
            target = null;
        }

        lock (gate)
        {
            emergency?.SetActive(current is not null
                || changes.Any(pending => pending.Target is not null));
        }

        if (generation is { } sharedGeneration)
        {
            sharedStopGeneration = sharedGeneration;
            sharedStop = result;
        }

        return result;
    }

    private void CheckChange(Change change)
    {
        change.Token.ThrowIfCancellationRequested();
        if (change.Target is not null)
        {
            emergency?.ThrowIfRequested();
        }
        ObjectDisposedException.ThrowIf(owner!.IsClosing || stopping, this);
        lock (gate)
        {
            if (change.Target is not null && change.ReleaseVersion != releaseVersion)
            {
                throw new SelectiveInputSupersededException();
            }

            if (change.Abandoned || (change.Stopping.Task.IsCompleted
                && clock.GetElapsedTime(change.RequestedAt) >= SelectiveInputHookStop.Timeout))
            {
                throw new TimeoutException("待機期限を過ぎた設定を登録できません。");
            }
        }
    }

    public void Wait(AutoResetEvent work, int milliseconds)
    {
        lock (gate)
        {
            if (changes.Count != 0)
            {
                return;
            }
        }

        if (current is not null)
        {
            current.Wait(work, milliseconds);
        }
        else
        {
            work.WaitOne(milliseconds);
        }
    }

    public void RefreshTargets()
    {
        if (cleanupFailure is null)
        {
            current?.RefreshTargets();
        }
    }

    public void Suspend() => current?.Suspend();

    public void Stop()
    {
        lock (gate)
        {
            stopping = true;
            while (changes.TryDequeue(out Change? change))
            {
                if (change.Token.IsCancellationRequested)
                {
                    // 実行終了に伴う取消を、予期しない破棄エラーへ置き換えません。
                    change.Completion.TrySetCanceled(change.Token);
                }
                else
                {
                    change.Completion.TrySetException(new ObjectDisposedException(GetType().Name));
                }
            }
        }

        Exception? failure = TakeFailure();
        (long generation, long startedAt) = owner!.StopContext;
        SelectiveInputStopResult result = sharedStopGeneration == generation && sharedStop is not null
            ? sharedStop : StopCurrent(generation == 0 ? clock.GetTimestamp() : startedAt);
        failure = SelectiveInputHookStop.Combine(result.Error, failure);
        sharedStopGeneration = owner.StopContext.Generation;
        sharedStop = new(result.Stopped, failure);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
