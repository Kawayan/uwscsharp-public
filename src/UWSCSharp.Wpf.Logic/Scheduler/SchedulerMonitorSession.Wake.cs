namespace UWSCSharp.Wpf;

internal interface ISchedulerWakeResources
{
    bool UpdateWake(DateTimeOffset? dueAt);
}

internal sealed record SchedulerWakeRequest(Guid Generation, Guid ConfigurationEpoch, Guid? Revision,
    SchedulerSettings Settings, DateTime Watermark, TimeZoneInfo Zone, DateTimeOffset Instant);

internal sealed partial class SchedulerRuntime
{
    internal DateTime? WakeWatermark => minutes.Watermark;

    internal Guid WakeConfigurationEpoch => configurationEpoch;
}

internal sealed partial class SchedulerMonitorSession
{
    private SchedulerWakeRequest? wakeRequest;
    private Exception? wakeError;

    internal Exception? WakeError
    {
        get
        {
            lock (sync)
            {
                return wakeError;
            }
        }
    }

    internal SchedulerWakeRequest? BeginWakePlanning(DateTimeOffset instant, TimeZoneInfo zone)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (preparing || preparationFailed || runtime?.IsActive != true || lease?.IsActive != true)
            {
                if (runtime?.IsActive == true && lease?.IsActive != true)
                {
                    Fail(new InvalidOperationException("復帰計画の監視所有権を失いました。"));
                }
                ResetWake();
                return null;
            }
            Poll(instant, zone);
            if (runtime?.IsActive != true || controller.Current?.Settings.WakeUp != true)
            {
                if (ResetWake())
                {
                    wakeError = null;
                }
                return null;
            }
            SchedulerSettingsSnapshot snapshot = controller.Current!;
            DateTime watermark = runtime.WakeWatermark!.Value;
            if (wakeRequest is not null && wakeRequest.Generation == lease!.Generation
                && wakeRequest.ConfigurationEpoch == runtime.WakeConfigurationEpoch
                && wakeRequest.Revision == snapshot.Revision && wakeRequest.Watermark == watermark
                && wakeRequest.Instant.UtcTicks / TimeSpan.TicksPerMinute
                    == instant.UtcTicks / TimeSpan.TicksPerMinute
                && wakeRequest.Settings.OneShotStates.Select(state => state.EntryId).ToHashSet()
                    .SetEquals(snapshot.Settings.OneShotStates.Select(state => state.EntryId))
                && SameWakeZone(wakeRequest.Zone, zone))
            {
                return wakeRequest;
            }
            if (!ResetWake())
            {
                return null;
            }
            wakeRequest = new(lease!.Generation, runtime.WakeConfigurationEpoch, snapshot.Revision, snapshot.Settings,
                watermark, zone, instant);
            return wakeRequest;
        }
    }

    internal bool ApplyWakePlan(SchedulerWakeRequest request, SchedulerWakePlan plan,
        DateTimeOffset instant, TimeZoneInfo zone)
    {
        lock (sync)
        {
            if (!ReferenceEquals(request, BeginWakePlanning(instant, zone)))
            {
                return false;
            }
            if (plan.DueAt is { } due && due <= instant)
            {
                wakeError = new InvalidOperationException("復帰候補の時刻が過ぎたため再計算が必要です。");
                wakeRequest = null;
                return false;
            }
            try
            {
                DateTimeOffset? wakeAt = plan.DueAt;
                if (wakeAt is { } target && target - instant > TimeSpan.FromMinutes(1))
                {
                    wakeAt = target.AddMinutes(-1);
                }
                bool supported = resources is ISchedulerWakeResources wake
                    ? wake.UpdateWake(wakeAt) : wakeAt is null;
                wakeError = supported ? null : new NotSupportedException("この環境は予約による自動復帰に対応していません。");
                return true;
            }
            catch (Exception error) when (Operational(error))
            {
                wakeError = error;
                if (ResetWake())
                {
                    wakeRequest = request;
                }
                return false;
            }
        }
    }

    internal void ReportWakeFailure(SchedulerWakeRequest request, Exception error)
    {
        lock (sync)
        {
            if (ReferenceEquals(wakeRequest, request))
            {
                if (ResetWake())
                {
                    wakeRequest = request;
                }
                wakeError = error;
            }
        }
    }

    private bool ResetWake()
    {
        wakeRequest = null;
        try
        {
            if (resources is ISchedulerWakeResources wake)
            {
                wake.UpdateWake(null);
            }
            return true;
        }
        catch (Exception error) when (Operational(error))
        {
            wakeError = error;
            LastError = error;
            preparationFailed = lease is not null;
            runtime?.Stop();
            return false;
        }
    }

    private static bool SameWakeZone(TimeZoneInfo left, TimeZoneInfo right) =>
        left.Id.Equals(right.Id, StringComparison.Ordinal) && left.HasSameRules(right);
}
