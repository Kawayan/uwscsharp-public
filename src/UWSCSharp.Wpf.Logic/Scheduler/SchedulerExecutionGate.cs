namespace UWSCSharp.Wpf;

/// <summary>
/// 予約4枠と同一プロセス・編集操作の排他を一緒に確保します。
/// </summary>
internal sealed class SchedulerExecutionGate
{
    internal const int MaximumScheduledExecutions = 4;
    private readonly object sync = new();
    private bool inputCleanupConfirmed;
    private bool interactive;
    private bool sameProcess;
    private int scheduled;
    private int unrecovered;

    internal bool IsSameProcessReserved
    {
        get
        {
            lock (sync)
            {
                return sameProcess;
            }
        }
    }

    internal bool CanEnterInteractive
    {
        get
        {
            lock (sync)
            {
                return inputCleanupConfirmed && unrecovered == 0
                    && !interactive && !sameProcess;
            }
        }
    }

    internal int ScheduledCount
    {
        get
        {
            lock (sync)
            {
                return scheduled;
            }
        }
    }

    internal void SetInputCleanupConfirmed(bool confirmed)
    {
        lock (sync)
        {
            inputCleanupConfirmed = confirmed;
        }
    }

    internal IDisposable? TryEnterInteractive()
    {
        lock (sync)
        {
            if (!inputCleanupConfirmed || unrecovered != 0 || interactive || sameProcess)
            {
                return null;
            }

            interactive = true;
            return new InteractiveReservation(this);
        }
    }

    internal Reservation? TryBeginDispatch()
    {
        lock (sync)
        {
            if (!inputCleanupConfirmed || unrecovered != 0
                || scheduled == MaximumScheduledExecutions)
            {
                return null;
            }

            scheduled++;
            bool allowsSameProcess = !interactive && !sameProcess;
            sameProcess |= allowsSameProcess;
            return new Reservation(this, allowsSameProcess);
        }
    }

    /// <summary>
    /// 引渡し前に枠を押さえ、回収確認後にだけ解放します。
    /// </summary>
    internal sealed class Reservation
    {
        private readonly SchedulerExecutionGate owner;
        private bool sameProcess;
        private bool bound;
        private bool released;
        private bool unrecovered;

        internal Reservation(SchedulerExecutionGate owner, bool allowsSameProcess)
        {
            this.owner = owner;
            sameProcess = allowsSameProcess;
            AllowsSameProcess = allowsSameProcess;
        }

        internal bool AllowsSameProcess
        {
            get;
        }

        internal void Bind(SchedulerExecutionMode mode)
        {
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(released, this);
                if (!owner.inputCleanupConfirmed || owner.unrecovered != 0
                    || !Enum.IsDefined(mode) || bound
                    || mode == SchedulerExecutionMode.SameProcess && !sameProcess)
                {
                    throw new InvalidOperationException("予約の実行枠を確定できません。");
                }

                bound = true;
                if (mode == SchedulerExecutionMode.SeparateProcess && sameProcess)
                {
                    sameProcess = false;
                    owner.sameProcess = false;
                }
            }
        }

        internal void MarkUnrecovered()
        {
            lock (owner.sync)
            {
                ObjectDisposedException.ThrowIf(released, this);
                if (!bound)
                {
                    throw new InvalidOperationException("未確定の予約枠を回収待ちにできません。");
                }

                if (!unrecovered)
                {
                    unrecovered = true;
                    owner.unrecovered++;
                }
            }
        }

        internal void ReleaseAfterCleanup()
        {
            lock (owner.sync)
            {
                if (released)
                {
                    return;
                }

                released = true;
                owner.scheduled--;
                if (unrecovered)
                {
                    owner.unrecovered--;
                }
                if (sameProcess)
                {
                    owner.sameProcess = false;
                }
            }
        }
    }

    private sealed class InteractiveReservation(SchedulerExecutionGate owner)
        : IDisposable
    {
        private bool released;

        public void Dispose()
        {
            lock (owner.sync)
            {
                if (!released)
                {
                    released = true;
                    owner.interactive = false;
                }
            }
        }
    }
}
