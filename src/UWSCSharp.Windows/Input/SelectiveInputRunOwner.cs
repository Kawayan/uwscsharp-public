namespace UWSCSharp.Windows;

/// <summary>
/// 実行全体の設定受付と終了要求を共有し、未解除の所有者を保持します。
/// </summary>
internal sealed class SelectiveInputRunOwner : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Func<SelectiveInputTarget, SelectiveInputDevices, ISelectiveInputHookSession> create;
    private readonly TimeProvider clock;
    private readonly SelectiveInputEmergencyStop emergency;
    private readonly bool ownsEmergency;
    private SelectiveInputRunSession session = null!;
    private SelectiveInputHookOwner owner = null!;
    private Task? closing;
    private int reportedFailures;

    internal SelectiveInputRunOwner(
        Func<SelectiveInputTarget, SelectiveInputDevices, ISelectiveInputHookSession> create,
        TimeProvider? clock = null, SelectiveInputEmergencyStop? emergency = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        this.create = create;
        this.clock = clock ?? TimeProvider.System;
        ownsEmergency = emergency is null;
        this.emergency = emergency ?? new SelectiveInputEmergencyStop();
        this.emergency.Attach(RequestEmergencyCleanupAsync);
        CreateOwner();
    }

    internal bool IsAlive
    {
        get
        {
            lock (gate)
            {
                return owner.IsAlive;
            }
        }
    }

    internal bool IsCleanupComplete
    {
        get
        {
            lock (gate)
            {
                return owner.IsCleanupComplete;
            }
        }
    }

    internal Task ChangeAsync(SelectiveInputTarget? target,
        SelectiveInputDevices devices, CancellationToken token)
    {
        lock (gate)
        {
            if (target is not null)
            {
                emergency.ThrowIfRequested();
            }

            ObjectDisposedException.ThrowIf(closing is not null, this);
            Exception? failure = TakeFailureCore();
            if (failure is not null && target is not null)
            {
                return Task.FromException(failure);
            }

            if (failure is not null && target is null)
            {
                return owner.Failure is not null
                    ? CleanupFaultedOwnerAsync(owner, failure)
                    : ReleaseAndReportAsync(session.ChangeAsync(target, devices, token), failure);
            }

            if (!owner.IsAlive)
            {
                // 終了済み所有者の待機イベントも回収してから世代を切り替えます。
                try
                {
                    owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch when (owner.Failure is not null)
                {
                    // 既に通知した障害を保持する旧所有者のイベントだけを回収します。
                }
                CreateOwner();
            }

            if (owner.Failure is not null)
            {
                if (target is null)
                {
                    return CleanupFaultedOwnerAsync(owner, failure);
                }
                var error = new InvalidOperationException(
                    "失敗した入力フックを明示解除してください。", owner.Failure);
                if (SelectiveInputOperationErrors.IsExpected(owner.Failure))
                {
                    WindowsOperationErrors.Record(error);
                }
                return Task.FromException(error);
            }

            return session.ChangeAsync(target, devices, token);
        }
    }

    internal Exception? TakeFailure()
    {
        lock (gate)
        {
            return TakeFailureCore();
        }
    }

    private Exception? TakeFailureCore()
    {
        (Exception? error, int count) = owner.ReadFailure();
        if (count <= reportedFailures)
        {
            error = null;
        }

        reportedFailures = count;
        return SelectiveInputHookStop.Combine(error, session.TakeFailure());
    }

    private void CreateOwner()
    {
        var next = new SelectiveInputRunSession(create, clock, emergency);
        session = next;
        owner = new SelectiveInputHookOwner(() => next, clock);
        session.Attach(owner);
        _ = ObserveCleanupAsync(owner);
        reportedFailures = 0;
    }

    private async Task CleanupFaultedOwnerAsync(SelectiveInputHookOwner previous, Exception? failure)
    {
        try
        {
            await previous.DisposeAsync().ConfigureAwait(false);
            if (failure is not null)
            {
                throw failure;
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(owner, previous))
                {
                    reportedFailures = previous.ReadFailure().Count;
                }
            }
        }
    }

    private static async Task ReleaseAndReportAsync(Task release, Exception failure)
    {
        try
        {
            await release.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw SelectiveInputHookStop.Combine(failure, error)!;
        }

        throw failure;
    }

    /// <summary>
    /// 最初の終了待機を共有します。自動で待機期限を延ばしません。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            emergency.MarkExecutionEnded();
            closing ??= DisposeRunAsync(owner);
            return new ValueTask(closing);
        }
    }

    private async Task DisposeRunAsync(SelectiveInputHookOwner previous)
    {
        try
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (WasReportedAndReleased(previous, error))
        {
            // 通知済みの過去の障害を、新しい解放失敗として重複報告しません。
        }
        finally
        {
            RetainEmergencyTarget();
            if (ownsEmergency)
            {
                await emergency.DisposeExecutionAsync().ConfigureAwait(false);
            }
        }
    }

    internal void RetainEmergencyTarget()
    {
        emergency.SetRetained(!owner.IsCleanupComplete);
        if (owner.CleanupCompleted.IsCompleted)
        {
            emergency.SetRetained(false);
        }
    }

    private async Task ObserveCleanupAsync(SelectiveInputHookOwner previous)
    {
        await previous.CleanupCompleted.ConfigureAwait(false);
        if (ReferenceEquals(Volatile.Read(ref owner), previous))
        {
            emergency.SetRetained(false);
        }
    }

    private Task RequestEmergencyCleanupAsync()
    {
        lock (gate)
        {
            if (closing is null)
            {
                return DisposeAsync().AsTask();
            }

            return closing.IsCompleted && !owner.IsCleanupComplete
                ? owner.DisposeAsync().AsTask() : closing;
        }
    }

    private bool WasReportedAndReleased(SelectiveInputHookOwner previous, Exception error)
    {
        lock (gate)
        {
            (Exception? saved, int count) = previous.ReadFailure();
            return !previous.IsAlive && count > 0 && count <= reportedFailures
                && ReferenceEquals(error, saved);
        }
    }

    internal Task RetryCleanupAsync()
    {
        lock (gate)
        {
            if (closing is null)
            {
                throw new InvalidOperationException("実行終了前の再回収は明示解除を使用してください。");
            }

            return owner.DisposeAsync().AsTask();
        }
    }
}
