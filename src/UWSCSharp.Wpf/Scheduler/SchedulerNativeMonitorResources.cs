using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// 監視ファイルを保持する間に、実Jobの準備と解放を行います。
/// </summary>
internal sealed class SchedulerNativeMonitorResources : ISchedulerMonitorResources, ISchedulerWakeResources
{
    private static readonly TimeSpan PreparationDrainTimeout = TimeSpan.FromSeconds(10);

    private WindowsScheduleWakeTimer? wakeTimer;
    private bool prepared;
    private readonly Func<CancellationToken, Task>? preparationCheck;

    internal SchedulerNativeMonitorResources(SchedulerMonitorLease lease,
        Func<CancellationToken, Task>? preparationCheck = null)
    {
        this.preparationCheck = preparationCheck;
        Generation = lease.Generation;
        Group = new SchedulerProcessGroup(lease.JobIdentity, lease.Handle);
    }

    internal Guid Generation
    {
        get;
    }

    internal SchedulerProcessGroup Group
    {
        get;
    }

    internal DateTimeOffset? WakeTime => wakeTimer?.ArmedAt;

    /// <summary>
    /// Jobに実行中のプロセスが残る間は所有を保持します。
    /// </summary>
    public bool CanRelease => Group.ActiveProcesses == 0;

    /// <summary>
    /// 以前の所有者の子孫を回収してから実行を許可します。
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (!prepared)
        {
            await Group.RecoverAsync(cancellationToken).ConfigureAwait(false);
            prepared = true;
        }
        else if (Group.ActiveProcesses == 0)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(PreparationDrainTimeout);
            await Group.WaitForDrainAsync(deadline.Token).ConfigureAwait(false);
        }
        if (preparationCheck is not null)
        {
            await preparationCheck(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Job自身の最終確認を通過してからハンドルを閉じます。
    /// </summary>
    public void Dispose()
    {
        UpdateWake(null);
        Group.Dispose();
    }

    /// <summary>
    /// 復帰要求はJobの実行中でも独立して取り消します。
    /// </summary>
    public bool UpdateWake(DateTimeOffset? dueAt)
    {
        if (dueAt is null)
        {
            WindowsScheduleWakeTimer? previous = wakeTimer;
            wakeTimer = null;
            if (previous is not null)
            {
                try
                {
                    previous.Cancel();
                }
                finally
                {
                    previous.Dispose();
                }
            }
            return true;
        }
        wakeTimer ??= new WindowsScheduleWakeTimer();
        try
        {
            return wakeTimer.Arm(dueAt.Value);
        }
        catch
        {
            wakeTimer.Dispose();
            wakeTimer = null;
            throw;
        }
    }
}
