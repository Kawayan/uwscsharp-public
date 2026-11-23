namespace UWSCSharp.Wpf;

/// <summary>
/// 条件の候補時刻と、探索を再開するための時刻を区別します。
/// </summary>
internal sealed record SchedulerWakePlan(
    DateTimeOffset? DueAt, bool IsCheckpoint = false, string? Diagnostic = null);

/// <summary>
/// 受付・一回の消費・OS操作をせず、未来の復帰候補を計算します。
/// </summary>
internal static class SchedulerWakePlanner
{
    internal const int MaximumExpressionEvaluations = 8192;
    internal const int MaximumLookAheadMinutes = 1440;
    internal const int MaximumCalendarDays = 63;
    private const int MaximumCheckpointMinutes = 4320;

    internal static SchedulerWakePlan Find(SchedulerSettings settings,
        DateTimeOffset instant, TimeZoneInfo zone, DateTime? watermark = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!settings.Enabled || !settings.WakeUp)
        {
            return new(null);
        }
        DateTime floor = SchedulerCalendar.LocalMinute(instant, zone);
        if (watermark is { } previous)
        {
            if (previous.Kind != DateTimeKind.Unspecified
                || previous.Ticks % TimeSpan.TicksPerMinute != 0)
            {
                throw new ArgumentException("観測済みのローカル分が不正です。", nameof(watermark));
            }
            if (previous > floor)
            {
                floor = previous;
            }
        }
        var calendar = new SchedulerCalendar(settings);
        var consumed = new HashSet<Guid>(settings.OneShotStates.Select(state => state.EntryId));
        var expressions = new List<SchedulerEntry>();
        var earliest = new SchedulerWakePlan(null);
        foreach (SchedulerEntry? entry in settings.Slots)
        {
            token.ThrowIfCancellationRequested();
            if (entry is not { Enabled: true } || consumed.Contains(entry.Id))
            {
                continue;
            }
            if (entry.Condition is SchedulerExpressionCondition)
            {
                expressions.Add(entry);
                continue;
            }
            SchedulerWakePlan candidate = FindExact(entry, floor, instant, zone, calendar, token);
            if (candidate.DueAt is not null
                && (earliest.DueAt is null || candidate.DueAt < earliest.DueAt))
            {
                earliest = candidate;
            }
        }
        if (expressions.Count == 0)
        {
            return earliest;
        }

        int evaluations = 0;
        DateTime minute = floor;
        for (int index = 0; index < MaximumLookAheadMinutes; index++)
        {
            token.ThrowIfCancellationRequested();
            if (!NextMinute(ref minute))
            {
                return earliest;
            }
            DateTimeOffset? utc = FutureUtc(minute, instant, zone);
            if (utc is null)
            {
                continue;
            }
            if (earliest.DueAt is not null && utc >= earliest.DueAt)
            {
                return earliest;
            }
            string? error = null;
            foreach (SchedulerEntry entry in expressions)
            {
                token.ThrowIfCancellationRequested();
                if (evaluations++ == MaximumExpressionEvaluations)
                {
                    return new(utc, true, "数値式の探索上限に達したため、この分に再確認します。");
                }
                SchedulerMatch match = calendar.Evaluate(entry.Id, minute);
                if (match.Kind == SchedulerMatchKind.Match)
                {
                    return new(utc);
                }
                if (match.Kind == SchedulerMatchKind.Error)
                {
                    error ??= $"数値式の行 {entry.Id}: {match.Error}";
                }
            }
            if (error is not null)
            {
                return new(utc, true, error);
            }
        }

        // 無一致を未来全体の無一致へ広げず、最初の未調査分で再確認します。
        return FindCheckpoint(minute, instant, zone, earliest, token);
    }

    private static SchedulerWakePlan FindCheckpoint(DateTime minute, DateTimeOffset instant,
        TimeZoneInfo zone, SchedulerWakePlan earliest, CancellationToken token)
    {
        for (int attempt = 0; attempt < MaximumCheckpointMinutes; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (!NextMinute(ref minute))
            {
                return earliest;
            }
            DateTimeOffset? utc = FutureUtc(minute, instant, zone);
            if (utc is not null)
            {
                return earliest.DueAt is not null && earliest.DueAt <= utc
                    ? earliest : new(utc, true, "探索期間の先を再確認します。");
            }
        }
        throw new InvalidOperationException("探索を再開する未来の時刻を計算できません。");
    }

    private static SchedulerWakePlan FindExact(SchedulerEntry entry, DateTime floor,
        DateTimeOffset instant, TimeZoneInfo zone, SchedulerCalendar calendar,
        CancellationToken token)
    {
        if (entry.Condition is SchedulerOneShotCondition once)
        {
            var local = once.Date.ToDateTime(once.Time, DateTimeKind.Unspecified);
            return new(local > floor ? FutureUtc(local, instant, zone) : null);
        }
        if (entry.Condition is not (SchedulerTimeCondition or SchedulerIntervalCondition))
        {
            return new(null);
        }
        DateTime day = floor.Date;
        // 暦と時間帯の変更で一致が先になる場合も、探索の終了を無一致と誤認しません。
        for (int index = 0; index < MaximumCalendarDays; index++)
        {
            int first;
            int step;
            if (entry.Condition is SchedulerTimeCondition time)
            {
                first = time.Time.Hour * 60 + time.Time.Minute;
                step = 1440;
            }
            else
            {
                var interval = (SchedulerIntervalCondition)entry.Condition;
                first = 0;
                step = interval.Value * (interval.Unit == SchedulerIntervalUnit.Hour ? 60 : 1);
            }
            for (int offset = first; offset < 1440; offset += step)
            {
                token.ThrowIfCancellationRequested();
                DateTime local = day.AddMinutes(offset);
                if (local <= floor || calendar.Evaluate(entry.Id, local).Kind != SchedulerMatchKind.Match)
                {
                    continue;
                }
                DateTimeOffset? utc = FutureUtc(local, instant, zone);
                if (utc is not null)
                {
                    return new(utc);
                }
            }
            if (day == DateTime.MaxValue.Date)
            {
                return new(null);
            }
            day = day.AddDays(1);
        }
        return FindCheckpoint(day.AddTicks(-TimeSpan.TicksPerMinute), instant, zone, new(null), token);
    }

    private static DateTimeOffset? FutureUtc(DateTime local, DateTimeOffset instant, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
        {
            return null;
        }
        TimeSpan[] offsets = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
        DateTimeOffset? earliest = null;
        foreach (TimeSpan offset in offsets)
        {
            long utcTicks = local.Ticks - offset.Ticks;
            if (utcTicks < DateTime.MinValue.Ticks || utcTicks > DateTime.MaxValue.Ticks)
            {
                continue;
            }
            DateTimeOffset utc = new DateTimeOffset(local, offset).ToUniversalTime();
            if (utc > instant && (earliest is null || utc < earliest))
            {
                earliest = utc;
            }
        }
        return earliest;
    }

    private static bool NextMinute(ref DateTime minute)
    {
        if (minute.Ticks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerMinute)
        {
            return false;
        }
        minute = minute.AddMinutes(1);
        return true;
    }
}
