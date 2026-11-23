using System.Windows.Threading;
using UWSCSharp.IO;

namespace UWSCSharp.Wpf;

/// <summary>
/// 監視の要求を実行先へ渡し、実行ごとの取消と回収を所有します。
/// </summary>
internal sealed partial class SchedulerRuntimeHost : IDisposable
{
    private static readonly TimeSpan WindowObservationInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan MonitorPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HistoryFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly TimeProvider clock;
    private readonly Func<TimeZoneInfo> timeZone;
    private readonly SchedulerMonitorSession monitor;
    private readonly Func<SchedulerDispatch, CancellationToken, Action, Task<SchedulerExecutionResult>> sameProcess;
    private readonly string executable;
    private readonly Func<int, Func<Task>, CancellationToken, Task>? keyboardGuard;
    private readonly SchedulerRuntimeDiagnostics? diagnostics;
    private readonly string logDirectory;
    private readonly SchedulerHistoryJournal history;
    private readonly Action startSound;
    private Exception? notificationError;
    private Task historyFlush = Task.CompletedTask;
    private readonly Dictionary<Guid, Execution> active = [];
    private readonly List<Execution> completed = [];
    private SchedulerNativeMonitorResources? native;
    private bool starting;
    private TaskCompletionSource? preparation;
    private bool stopping;
    private bool stopWaiting;
    private bool disposed;

    internal SchedulerRuntimeHost(Dispatcher dispatcher,
        SchedulerSettingsController settings, SchedulerApplicationSession application,
        SchedulerExecutionGate gate,
        Func<SchedulerDispatch, CancellationToken, Action, Task<SchedulerExecutionResult>> sameProcess,
        string executable, string logDirectory, string? leasePath = null,
        TimeProvider? clock = null, Func<TimeZoneInfo>? timeZone = null,
        Func<int, Func<Task>, CancellationToken, Task>? keyboardGuard = null,
        Func<CancellationToken, Task>? preparationCheck = null,
        Func<CancellationToken, Task<IReadOnlyList<UWSCSharp.Windows.WindowsObservedWindow>>>? windowSource = null,
        Action? startSound = null,
        SchedulerRuntimeDiagnostics? diagnostics = null)
    {
        this.dispatcher = dispatcher;
        this.sameProcess = sameProcess;
        this.executable = executable;
        this.keyboardGuard = keyboardGuard;
        this.diagnostics = diagnostics;
        this.logDirectory = logDirectory;
        this.startSound = startSound ?? UWSCSharp.Windows.WindowsScheduleStartSound.Play;
        this.clock = clock ?? TimeProvider.System;
        this.timeZone = timeZone ?? (() => TimeZoneInfo.Local);
        var storage = new SchedulerManagedStorage(logDirectory);
        history = new SchedulerHistoryJournal(storage.SaveHistoryAsync);
        monitor = new SchedulerMonitorSession(settings, application, gate, leasePath, lease =>
        {
            native = new SchedulerNativeMonitorResources(lease, preparationCheck);
            diagnostics?.MonitorOwned?.Invoke(lease.Dispose);
            return native;
        });
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = MonitorPollInterval
        };
        timer.Tick += (_, _) => Poll();
        this.windowSource = windowSource ?? (token => Task.FromResult(
            UWSCSharp.Windows.WindowsWindowObserver.Capture(token)));
        windowTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = WindowObservationInterval
        };
        windowTimer.Tick += (_, _) => QueueWindowObservation();
    }

    internal bool OwnsMonitor => monitor.OwnsMonitor;

    internal bool IsActive => monitor.IsActive;

    internal int ActiveCount => active.Count;

    internal Exception? PersistenceError => history.LastError;

    internal bool IsSavingHistory => history.IsWriting;

    internal Exception? NotificationError => Volatile.Read(ref notificationError);

    internal bool IsPolling => timer.IsEnabled;

    internal bool CanClose => !starting && active.Count == 0
        && !monitor.OwnsMonitor && windowObservation.IsCompleted && wakePlanning.IsCompleted;

    internal Exception? LastError
    {
        get; private set;
    }

    internal IReadOnlyList<Execution> Executions => [.. active.Values, .. completed];

    internal IReadOnlyList<uint> OwnedProcessIds
    {
        get
        {
            VerifyAccess();
            return native?.Group.GetProcessIds() ?? [];
        }
    }

    internal async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        VerifyAccess();
        if (starting || stopWaiting || !windowObservation.IsCompleted || !wakePlanning.IsCompleted
            || stopping && active.Count != 0)
        {
            throw new InvalidOperationException("予約実行ホストを開始中です。");
        }
        starting = true;
        preparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        stopping = false;
        try
        {
            bool started = await monitor.TryStartAsync(clock.GetUtcNow(), timeZone(), cancellationToken);
            LastError = monitor.LastError;
            if (started && !stopping)
            {
                timer.Start();
                DispatchAvailable();
                QueueWindowObservation();
                QueueWakePlanning();
            }
            return started && !stopping;
        }
        finally
        {
            starting = false;
            preparation.TrySetResult();
        }
    }

    internal void Poll()
    {
        VerifyAccess();
        QueueHistoryFlush();
        try
        {
            monitor.Poll(clock.GetUtcNow(), timeZone());
            LastError = monitor.LastError;
            if (!monitor.OwnsMonitor)
            {
                CancelAll();
            }
            if (!stopping)
            {
                DispatchAvailable();
            }
            QueueWindowObservation();
            QueueWakePlanning();
            if (!monitor.IsActive && active.Count == 0
                && !history.HasPending && historyFlush.IsCompleted)
            {
                timer.Stop();
            }
        }
        catch (Exception error)
        {
            LastError = error;
            monitor.StopAccepting();
            if (!monitor.OwnsMonitor)
            {
                CancelAll();
            }
            timer.Stop();
            CancelWindowObservation();
            CancelWakePlanning();
        }
    }

    internal void StopAccepting()
    {
        VerifyAccess();
        monitor.StopAccepting();
        CancelWindowObservation();
        CancelWakePlanning();
        if (active.Count == 0 && !history.HasPending && historyFlush.IsCompleted)
        {
            timer.Stop();
        }
    }

    internal bool Cancel(Guid occurrenceId)
    {
        VerifyAccess();
        if (!active.TryGetValue(occurrenceId, out Execution? execution)
            || execution.Finished.Task.IsCompleted)
        {
            return false;
        }
        execution.RequestCancellation();
        return true;
    }

    internal async Task<bool> StopAsync(TimeSpan timeout)
    {
        VerifyAccess();
        if (stopWaiting)
        {
            throw new InvalidOperationException("予約実行ホストは停止を待機中です。");
        }
        stopWaiting = true;
        try
        {
            stopping = true;
            timer.Stop();
            monitor.StopAccepting();
            CancelWindowObservation();
            CancelWakePlanning();
            CancelAll();
            Task[] waiting = [.. active.Values.Select(value => value.Finished.Task)
                .Append(preparation?.Task ?? Task.CompletedTask).Append(windowObservation).Append(wakePlanning)];
            try
            {
                await Task.WhenAll(waiting).WaitAsync(timeout);
            }
            catch (TimeoutException error)
            {
                LastError = error;
                return false;
            }
            monitor.StopAccepting();
            using var persistenceDeadline = new CancellationTokenSource(HistoryFlushTimeout);
            try
            {
                await Task.Run(() => history.FlushAsync(persistenceDeadline.Token))
                    .WaitAsync(persistenceDeadline.Token);
            }
            catch (OperationCanceledException error) when (persistenceDeadline.IsCancellationRequested)
            {
                // 履歴の期限切れを、実行資源が未回収であることとは扱いません。
                history.RecordFailure(new TimeoutException("予約履歴の終了時保存が5秒で完了しませんでした。", error));
            }
            return CanClose;
        }
        finally
        {
            stopWaiting = false;
        }
    }

    private void DispatchAvailable()
    {
        while (!stopping && active.Count < SchedulerExecutionGate.MaximumScheduledExecutions)
        {
            SchedulerDispatch? dispatch = monitor.TryDispatch();
            if (dispatch is null)
            {
                return;
            }
            var execution = new Execution(dispatch);
            active.Add(dispatch.Request.OccurrenceId, execution);
            history.Enqueue(execution.HistorySnapshot);
            QueueHistoryFlush();
            _ = ExecuteAsync(execution);
        }
    }

    private async Task ExecuteAsync(Execution execution)
    {
        var notification = new SchedulerStartNotification(execution.Dispatch.Beep, startSound);
        SchedulerExecutionResult result;
        try
        {
            if (execution.Cancellation.IsCancellationRequested)
            {
                result = SchedulerExecutionResult.Completed(130, false, true, true);
            }
            else if (execution.Dispatch.Request.Entry.Mode == SchedulerExecutionMode.SameProcess)
            {
                result = await sameProcess(execution.Dispatch, execution.Cancellation.Token,
                    ObserveStarted);
            }
            else if (native?.Generation == execution.Dispatch.Generation)
            {
                SchedulerWorkerRunResult worker = await SchedulerWorkerClient.RunAsync(
                    native.Group, executable, execution.Dispatch, logDirectory,
                    execution.Cancellation.Token, keyboardGuard: keyboardGuard,
                    onStarted: ObserveStarted,
                    onControlConnected: diagnostics?.WorkerControlConnected is null ? null
                        : close => diagnostics.WorkerControlConnected(execution.Dispatch, close));
                result = worker.Result;
            }
            else
            {
                throw new InvalidOperationException("予約の実資源と監視世代が一致しません。");
            }
        }
        catch (Exception error)
        {
            LastError = error;
            result = SchedulerExecutionResult.Unknown(execution.Timeline.StartedAt is not null,
                null, false, ScriptLogHost.FormatDiagnostic(error));
        }

        bool endedConfirmed = execution.Dispatch.Request.Entry.Mode == SchedulerExecutionMode.SameProcess
            || result.Outcome != SchedulerExecutionOutcome.Unknown
            || result.ExitCode is not null || result.ResourcesRecovered;
        execution.ObserveResult(clock.GetUtcNow(), result, endedConfirmed);
        history.Enqueue(execution.HistorySnapshot);
        QueueHistoryFlush();
        execution.Result = result;
        try
        {
            execution.ResultSaved = monitor.Complete(execution.Dispatch, result);
            if (!execution.ResultSaved)
            {
                LastError = monitor.LastError ?? new InvalidOperationException("予約の結果を保存できません。");
            }
            if (result.ResourcesRecovered)
            {
                active.Remove(execution.Dispatch.Request.OccurrenceId);
                execution.Cancellation.Dispose();
                completed.Add(execution);
                if (completed.Count > 100)
                {
                    completed.RemoveAt(0);
                }
            }
        }
        catch (Exception error)
        {
            LastError = error;
        }
        finally
        {
            execution.Finished.TrySetResult();
        }

        void ObserveStarted()
        {
            execution.ObserveStarted(clock.GetUtcNow());
            history.Enqueue(execution.HistorySnapshot);
            if (notification.NotifyStarted())
            {
                Volatile.Write(ref notificationError, notification.Error);
            }
        }
    }

    private void QueueHistoryFlush()
    {
        if (history.HasPending && historyFlush.IsCompleted)
        {
            historyFlush = Task.Run(() => history.FlushAsync());
        }
    }

    private void CancelAll()
    {
        foreach (Execution execution in active.Values.ToArray())
        {
            execution.RequestCancellation();
        }
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed)
        {
            return;
        }
        if (starting || active.Count != 0 || !windowObservation.IsCompleted || !wakePlanning.IsCompleted)
        {
            throw new InvalidOperationException("予約の実行と回収を待ってからホストを解放してください。");
        }
        timer.Stop();
        CancelWindowObservation();
        CancelWakePlanning();
        monitor.Dispose();
        disposed = true;
    }

    private void VerifyAccess()
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    internal sealed class Execution(SchedulerDispatch dispatch)
    {
        private readonly object historyState = new();
        private SchedulerHistoryRecord history = SchedulerHistoryRecord.Capture(dispatch);

        internal SchedulerHistoryRecord HistorySnapshot
        {
            get
            {
                lock (historyState)
                {
                    return history;
                }
            }
        }

        internal void ObserveStarted(DateTimeOffset instant)
        {
            lock (historyState)
            {
                Timeline.ObserveStarted(instant);
                history = history.MarkStarted(instant);
            }
        }

        internal void ObserveResult(DateTimeOffset instant,
            SchedulerExecutionResult result, bool endedConfirmed)
        {
            lock (historyState)
            {
                Timeline.ObserveResult(instant, endedConfirmed);
                history = history.Complete(result, instant, endedConfirmed);
            }
        }

        internal SchedulerDispatch Dispatch { get; } = dispatch;

        internal SchedulerExecutionTimeline Timeline { get; } = new(dispatch.Request.ObservedAt);

        internal CancellationTokenSource Cancellation { get; } = new();

        internal TaskCompletionSource Finished
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SchedulerExecutionResult? Result
        {
            get; set;
        }

        internal bool ResultSaved
        {
            get; set;
        }

        internal void RequestCancellation()
        {
            if (!Finished.Task.IsCompleted)
            {
                Cancellation.Cancel();
            }
        }
    }
}
