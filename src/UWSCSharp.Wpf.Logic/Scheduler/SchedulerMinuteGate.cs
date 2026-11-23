namespace UWSCSharp.Wpf;

internal enum SchedulerMinuteStatus
{
    Inactive,
    Waiting,
    Due,
    ClockBehind,
    TimeZoneChanged
}

internal sealed record SchedulerMinuteObservation(SchedulerMinuteStatus Status, DateTime Minute);

/// <summary>
/// 監視所有者が順に呼び、分の再受付と時計変更を管理します。
/// </summary>
internal sealed class SchedulerMinuteGate
{
    private readonly SchedulerApplicationSession applicationSession;
    private TimeZoneInfo? zone;

    internal SchedulerMinuteGate(SchedulerApplicationSession? applicationSession = null)
    {
        this.applicationSession = applicationSession ?? new SchedulerApplicationSession();
    }

    internal bool IsActive
    {
        get; private set;
    }

    internal DateTime? Watermark
    {
        get; private set;
    }

    internal bool Start(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        DateTime minute = SchedulerCalendar.LocalMinute(instant, timeZone);
        if (zone is null || !SameZone(zone, timeZone)
            || Watermark is null || minute > Watermark.Value)
        {
            Watermark = minute;
        }

        zone = timeZone;
        IsActive = true;
        return applicationSession.TryAcceptStartup();
    }

    internal void Stop() => IsActive = false;

    internal SchedulerMinuteObservation Observe(DateTimeOffset instant,
        TimeZoneInfo observedZone)
    {
        DateTime minute = SchedulerCalendar.LocalMinute(instant, observedZone);
        if (!IsActive)
        {
            return new SchedulerMinuteObservation(SchedulerMinuteStatus.Inactive, minute);
        }

        if (!SameZone(zone!, observedZone))
        {
            Stop();
            return new SchedulerMinuteObservation(SchedulerMinuteStatus.TimeZoneChanged, minute);
        }

        if (minute <= Watermark!.Value)
        {
            return new SchedulerMinuteObservation(minute < Watermark.Value
                ? SchedulerMinuteStatus.ClockBehind : SchedulerMinuteStatus.Waiting, minute);
        }

        Watermark = minute;
        return new SchedulerMinuteObservation(SchedulerMinuteStatus.Due, minute);
    }

    private static bool SameZone(TimeZoneInfo left, TimeZoneInfo right) =>
        left.Id.Equals(right.Id, StringComparison.Ordinal) && left.HasSameRules(right);
}
