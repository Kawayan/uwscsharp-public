using System.IO;

namespace UWSCSharp.Wpf;

internal sealed record SchedulerDispatch(
    Guid Generation, SchedulerExecutionRequest Request, bool Beep = false);

/// <summary>
/// 監視所有・受付・実行枠を接続し、回収まで所有を維持します。
/// </summary>
internal sealed partial class SchedulerMonitorSession : IDisposable
{
    private readonly object sync = new();
    private readonly SchedulerSettingsController controller;
    private readonly SchedulerApplicationSession application;
    private readonly SchedulerExecutionGate gate;
    private readonly string? leasePath;
    private readonly Func<SchedulerMonitorLease, ISchedulerMonitorResources>? resourcesFactory;
    private ISchedulerMonitorResources? resources;
    private bool preparing;
    private bool preparationFailed;
    private bool stopDuringPreparation;
    private readonly Dictionary<Guid, ActiveDispatch> dispatched = [];
    private SchedulerMonitorLease? lease;
    private SchedulerRuntime? runtime;
    private bool disposed;
    private int timerRunning;

    internal SchedulerMonitorSession(SchedulerSettingsController controller,
        SchedulerApplicationSession application, SchedulerExecutionGate gate,
        string? leasePath = null,
        Func<SchedulerMonitorLease, ISchedulerMonitorResources>? resourcesFactory = null)
    {
        this.controller = controller;
        this.application = application;
        this.gate = gate;
        this.leasePath = leasePath;
        this.resourcesFactory = resourcesFactory;
    }

    internal bool OwnsMonitor
    {
        get
        {
            lock (sync)
            {
                return lease?.IsActive == true;
            }
        }
    }

    internal bool IsActive
    {
        get
        {
            lock (sync)
            {
                return runtime?.IsActive == true;
            }
        }
    }

    internal Exception? LastError
    {
        get; private set;
    }

    internal bool TryStart(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (resourcesFactory is not null)
            {
                throw new InvalidOperationException("実資源を持つ監視は非同期で開始してください。");
            }

            try
            {
                lease ??= SchedulerMonitorLease.TryAcquire(leasePath);
                if (lease is null)
                {
                    return false;
                }

                return StartRuntime(instant, timeZone);
            }
            catch (Exception error) when (Operational(error))
            {
                Fail(error);
                return false;
            }
        }
    }

    internal async Task<bool> TryStartAsync(DateTimeOffset instant,
        TimeZoneInfo timeZone, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (resourcesFactory is null)
        {
            return TryStart(instant, timeZone);
        }

        ISchedulerMonitorResources preparingResources;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (preparing)
            {
                throw new InvalidOperationException("予約の実資源を準備中です。");
            }

            try
            {
                lease ??= SchedulerMonitorLease.TryAcquire(leasePath);
                if (lease is null)
                {
                    return false;
                }

                preparing = true;
                stopDuringPreparation = false;
                resources ??= resourcesFactory(lease)
                    ?? throw new InvalidOperationException("予約の実資源を作成できません。");
                preparingResources = resources;
            }
            catch (Exception error)
            {
                FailPreparation(error);
                if (!Operational(error))
                {
                    throw;
                }
                return false;
            }
        }

        try
        {
            await preparingResources.PrepareAsync(cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                preparing = false;
                preparationFailed = false;
                if (stopDuringPreparation || cancellationToken.IsCancellationRequested)
                {
                    runtime?.Stop();
                    ReleaseIfIdle();
                    return false;
                }

                try
                {
                    return StartRuntime(instant, timeZone);
                }
                catch (Exception error) when (Operational(error))
                {
                    Fail(error);
                    return false;
                }
            }
        }
        catch (Exception error)
        {
            lock (sync)
            {
                FailPreparation(error);
            }
            if (!Operational(error) && error is not OperationCanceledException)
            {
                throw;
            }
            return false;
        }
    }

    private bool StartRuntime(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        runtime ??= new SchedulerRuntime(controller,
            () => lease?.IsActive == true, application);
        controller.Reload();
        bool started = runtime.Start(instant, timeZone);
        LastError = runtime.LastError;
        ReleaseIfIdle();
        return started;
    }

    private void FailPreparation(Exception error)
    {
        preparing = false;
        preparationFailed = lease is not null;
        LastError = error;
        runtime?.Stop();
    }

    internal void Poll(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (preparing || preparationFailed || runtime?.IsActive != true)
            {
                return;
            }

            try
            {
                controller.Reload();
                runtime.Tick(instant, timeZone);
                LastError = runtime.LastError;
                ReleaseIfIdle();
            }
            catch (Exception error) when (Operational(error))
            {
                Fail(error);
            }
        }
    }

    internal SchedulerDispatch? TryDispatch()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (preparing || preparationFailed
                || lease?.IsActive != true || runtime?.IsActive != true)
            {
                return null;
            }

            SchedulerExecutionGate.Reservation? reservation = gate.TryBeginDispatch();
            if (reservation is null)
            {
                return null;
            }

            SchedulerExecutionRequest? request = runtime.TryTake(
                externalSameProcessBusy: !reservation.AllowsSameProcess);
            if (request is null)
            {
                reservation.ReleaseAfterCleanup();
                LastError = runtime.LastError;
                ReleaseIfIdle();
                return null;
            }

            try
            {
                reservation.Bind(request.Entry.Mode);
            }
            catch (InvalidOperationException error)
            {
                runtime.Complete(request.OccurrenceId, succeeded: false);
                reservation.ReleaseAfterCleanup();
                Fail(error);
                return null;
            }

            dispatched.Add(request.OccurrenceId, new ActiveDispatch(request, reservation));
            return new SchedulerDispatch(lease.Generation, request,
                controller.Current!.Settings.Beep);
        }
    }

    internal bool Complete(SchedulerDispatch dispatch, bool succeeded,
        bool resourcesRecovered) => resourcesRecovered && Complete(dispatch,
            SchedulerExecutionResult.Completed(succeeded ? 0 : 1, started: true,
                cancellationCompleted: false, resourcesRecovered: true));

    internal bool Complete(SchedulerDispatch dispatch, SchedulerExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ActiveDispatch? active = FindDispatch(dispatch);
            if (active is null || active.Result is not null)
            {
                return false;
            }

            active.Result = result;
            if (!result.ResourcesRecovered)
            {
                active.Reservation.MarkUnrecovered();
            }

            bool saved = runtime!.ReportResult(dispatch.Request.OccurrenceId, result);
            LastError = runtime.LastError;
            if (result.ResourcesRecovered)
            {
                ReleaseRecovered(dispatch, active);
            }

            return saved;
        }
    }

    internal bool ConfirmRecovery(SchedulerDispatch dispatch)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ActiveDispatch? active = FindDispatch(dispatch);
            if (active?.Result is null)
            {
                return false;
            }

            ReleaseRecovered(dispatch, active);
            return true;
        }
    }

    private ActiveDispatch? FindDispatch(SchedulerDispatch dispatch) =>
        lease?.Generation == dispatch.Generation && runtime is not null
        && dispatched.TryGetValue(dispatch.Request.OccurrenceId, out ActiveDispatch? active)
        && ReferenceEquals(active.Request, dispatch.Request) ? active : null;

    private void ReleaseRecovered(SchedulerDispatch dispatch, ActiveDispatch active)
    {
        runtime!.ReleaseRecovered(dispatch.Request.OccurrenceId);
        active.Reservation.ReleaseAfterCleanup();
        dispatched.Remove(dispatch.Request.OccurrenceId);
        ReleaseIfIdle();
    }

    private sealed class ActiveDispatch(SchedulerExecutionRequest request,
        SchedulerExecutionGate.Reservation reservation)
    {
        internal SchedulerExecutionRequest Request { get; } = request;

        internal SchedulerExecutionGate.Reservation Reservation { get; } = reservation;

        internal SchedulerExecutionResult? Result
        {
            get; set;
        }
    }

    internal void StopAccepting()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stopDuringPreparation = true;
            runtime?.Stop();
            LastError = runtime?.LastError ?? LastError;
            ReleaseIfIdle();
        }
    }

    internal async Task RunTimerAsync(TimeProvider timeProvider,
        Func<TimeZoneInfo> timeZone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(timeZone);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (timerRunning != 0)
            {
                throw new InvalidOperationException("予約の監視タイマーは既に動作中です。");
            }

            timerRunning = 1;
        }

        var timer = new SchedulerSerialTimer(timeProvider);
        try
        {
            await timer.RunAsync(_ =>
            {
                Poll(timeProvider.GetUtcNow(), timeZone());
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            lock (sync)
            {
                LastError = error;
            }

            throw;
        }
        finally
        {
            try
            {
                StopAccepting();
            }
            finally
            {
                Volatile.Write(ref timerRunning, 0);
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            if (preparing || preparationFailed || dispatched.Count != 0
                || Volatile.Read(ref timerRunning) != 0)
            {
                throw new InvalidOperationException(
                    "予約の資源回収を確認するまで監視所有権を解放できません。");
            }

            runtime?.Stop();
            if (!ResetWake())
            {
                throw new InvalidOperationException("復帰タイマーの取消を確認できません。");
            }
            if (!ReleaseResources())
            {
                throw new InvalidOperationException("予約の実資源の回収を確認できません。");
            }

            lease?.Dispose();
            lease = null;
            runtime = null;
            disposed = true;
        }
    }

    private void ReleaseIfIdle()
    {
        if (runtime?.IsActive != true && !ResetWake())
        {
            return;
        }
        if (!preparing && !preparationFailed
            && runtime?.IsActive != true && dispatched.Count == 0
            && ReleaseResources())
        {
            lease?.Dispose();
            lease = null;
            runtime = null;
        }
    }

    private bool ReleaseResources()
    {
        if (resources is null)
        {
            return true;
        }

        try
        {
            if (!resources.CanRelease)
            {
                return false;
            }

            resources.Dispose();
            resources = null;
            return true;
        }
        catch (Exception error) when (Operational(error))
        {
            preparationFailed = true;
            LastError = error;
            return false;
        }
    }

    private void Fail(Exception error)
    {
        LastError = error;
        runtime?.Stop();
        ReleaseIfIdle();
    }

    private static bool Operational(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException
            or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException;
}
