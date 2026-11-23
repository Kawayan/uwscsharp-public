using System.IO;

namespace UWSCSharp.Wpf;

internal enum SchedulerWorkerPhase
{
    AwaitingReady,
    Ready,
    Starting,
    Running,
    Cancelling,
    Completed,
    Faulted
}

/// <summary>
/// 本体側の準備・開始・取消・完了を一度だけ順に進めます。
/// </summary>
internal sealed class SchedulerWorkerHandshake
{
    private readonly object sync = new();
    private readonly SchedulerWorkerIdentity identity;
    private readonly int processId;
    private SchedulerWorkerPhase phase;
    private bool cancellationRequested;
    private bool started;
    private bool startCommandCreated;

    internal SchedulerWorkerHandshake(SchedulerWorkerIdentity identity, int processId)
    {
        if (identity.Generation == Guid.Empty || identity.OccurrenceId == Guid.Empty)
        {
            throw new ArgumentException("予約workerの識別子は空にできません。", nameof(identity));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        this.identity = identity;
        this.processId = processId;
    }

    internal SchedulerWorkerPhase Phase
    {
        get
        {
            lock (sync)
            {
                return phase;
            }
        }
    }

    internal bool HasStarted
    {
        get
        {
            lock (sync)
            {
                return started;
            }
        }
    }

    internal void AcceptReady(SchedulerWorkerMessage.Ready message)
    {
        lock (sync)
        {
            Match(message);
            if (phase != SchedulerWorkerPhase.AwaitingReady || message.ProcessId != processId)
            {
                throw new InvalidDataException("予約workerの準備通知が順序またはPIDと一致しません。");
            }

            phase = SchedulerWorkerPhase.Ready;
        }
    }

    internal SchedulerWorkerMessage CreateNextCommand()
    {
        lock (sync)
        {
            if (phase != SchedulerWorkerPhase.Ready)
            {
                throw new InvalidOperationException("予約workerの準備完了を確認していません。");
            }

            if (cancellationRequested)
            {
                phase = SchedulerWorkerPhase.Cancelling;
                return new SchedulerWorkerMessage.Cancel(identity);
            }

            startCommandCreated = true;
            phase = SchedulerWorkerPhase.Starting;
            return new SchedulerWorkerMessage.Start(identity);
        }
    }

    internal SchedulerWorkerMessage.Cancel? RequestCancellation()
    {
        lock (sync)
        {
            cancellationRequested = true;
            if (phase is not (SchedulerWorkerPhase.Starting or SchedulerWorkerPhase.Running))
            {
                return null;
            }

            phase = SchedulerWorkerPhase.Cancelling;
            return new SchedulerWorkerMessage.Cancel(identity);
        }
    }

    internal void AcceptStarted(SchedulerWorkerMessage.Started message)
    {
        lock (sync)
        {
            Match(message);
            if (!startCommandCreated || started
                || phase is not (SchedulerWorkerPhase.Starting or SchedulerWorkerPhase.Cancelling))
            {
                throw new InvalidDataException("予約workerの開始通知が実行の状態と一致しません。");
            }

            started = true;
            if (phase == SchedulerWorkerPhase.Starting)
            {
                phase = SchedulerWorkerPhase.Running;
            }
        }
    }

    internal void AcceptCompletion(SchedulerWorkerMessage.Completed message)
    {
        lock (sync)
        {
            Match(message);
            if (phase is not (SchedulerWorkerPhase.Running or SchedulerWorkerPhase.Cancelling)
                || !started && (startCommandCreated || !message.CancellationCompleted)
                || message.CancellationCompleted && message.ExitCode != 130)
            {
                throw new InvalidDataException("予約workerの完了通知が実行の状態と一致しません。");
            }

            // 通信上の完了と入力・所有Jobの回収は呼出し元で別々に確認します。
            phase = SchedulerWorkerPhase.Completed;
        }
    }

    internal void Fault()
    {
        lock (sync)
        {
            if (phase != SchedulerWorkerPhase.Completed)
            {
                phase = SchedulerWorkerPhase.Faulted;
            }
        }
    }

    private void Match(SchedulerWorkerMessage message)
    {
        if (message.Identity != identity)
        {
            throw new InvalidDataException("予約workerの通知が別の世代または発生識別子です。");
        }
    }
}
