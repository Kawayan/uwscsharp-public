using System.IO;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

internal enum SchedulerMatchKind
{
    NotApplicable,
    NoMatch,
    Match,
    Error
}

internal sealed record SchedulerMatch(SchedulerMatchKind Kind, string? Error = null);

/// <summary>
/// 時刻条件の事前検査と判定を行い、受付や消費の状態を変更しません。
/// </summary>
internal sealed class SchedulerCalendar
{
    private static readonly string[] VariableNames =
        ["YY", "MM", "DD", "HH", "NN", "WW", "YMDNN"];
    private readonly Dictionary<Guid, SchedulerEntry> entries = [];
    private readonly Dictionary<Guid, CompiledNumericExpression> expressions = [];

    internal SchedulerCalendar(SchedulerSettings settings)
    {
        foreach (SchedulerEntry? entry in settings.Slots)
        {
            if (entry is null || !entry.Enabled)
            {
                continue;
            }

            entries.Add(entry.Id, entry);
            if (entry.Condition is SchedulerExpressionCondition expression)
            {
                try
                {
                    expressions.Add(entry.Id, CompiledNumericExpression.Compile(
                        expression.Source, VariableNames));
                }
                catch (InvalidOperationException error)
                {
                    throw new InvalidDataException(
                        $"予約の数値式が不正です。行: {entry.Id}", error);
                }
            }
        }
    }

    internal static DateTime LocalMinute(DateTimeOffset instant, TimeZoneInfo zone)
    {
        DateTime local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        return new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerMinute,
            DateTimeKind.Unspecified);
    }

    internal SchedulerMatch Evaluate(Guid id, DateTime localMinute,
        bool consumed = false)
    {
        if (localMinute.Kind != DateTimeKind.Unspecified
            || localMinute.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("ローカル暦の分単位の日時を指定してください。",
                nameof(localMinute));
        }

        if (!entries.TryGetValue(id, out SchedulerEntry? entry))
        {
            return new SchedulerMatch(SchedulerMatchKind.NotApplicable);
        }

        bool match;
        switch (entry.Condition)
        {
            case SchedulerStartupCondition or SchedulerWindowCondition:
                return new SchedulerMatch(SchedulerMatchKind.NotApplicable);
            case SchedulerIntervalCondition interval:
                match = interval.Unit == SchedulerIntervalUnit.Minute
                    ? (localMinute.Hour * 60 + localMinute.Minute) % interval.Value == 0
                    : localMinute.Minute == 0 && localMinute.Hour % interval.Value == 0;
                break;
            case SchedulerTimeCondition time:
                match = TimeOnly.FromDateTime(localMinute) == time.Time
                    && (time.DayKind == SchedulerDayKind.EveryDay
                        || time.Days.Contains(time.DayKind == SchedulerDayKind.Weekdays
                            ? (int)localMinute.DayOfWeek : localMinute.Day));
                break;
            case SchedulerOneShotCondition oneShot:
                match = !consumed && DateOnly.FromDateTime(localMinute) == oneShot.Date
                    && TimeOnly.FromDateTime(localMinute) == oneShot.Time;
                break;
            case SchedulerExpressionCondition:
                try
                {
                    var variables = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["YY"] = localMinute.Year,
                        ["MM"] = localMinute.Month,
                        ["DD"] = localMinute.Day,
                        ["HH"] = localMinute.Hour,
                        ["NN"] = localMinute.Minute,
                        ["WW"] = (int)localMinute.DayOfWeek,
                        ["YMDNN"] = (localMinute.Ticks - new DateTime(2000, 1, 1).Ticks)
                            / TimeSpan.TicksPerMinute
                    };
                    match = expressions[id].Evaluate(variables) == 1.0;
                }
                catch (InvalidOperationException error)
                {
                    return new SchedulerMatch(SchedulerMatchKind.Error, error.Message);
                }

                break;
            default:
                throw new InvalidOperationException("未対応の予約条件です。");
        }

        return new SchedulerMatch(match ? SchedulerMatchKind.Match : SchedulerMatchKind.NoMatch);
    }
}
