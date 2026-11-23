using System.IO;

namespace UWSCSharp.Wpf;

internal enum SchedulerOneShotStatus
{
    Accepted,
    Started,
    Succeeded,
    Failed,
    Cancelled,
    Unknown
}

/// <summary>
/// 一回条件の消費と実行結果を、条件の文字列から分離します。
/// </summary>
internal sealed class SchedulerOneShotState
{
    internal SchedulerOneShotState(Guid entryId, DateOnly date, TimeOnly time,
        Guid occurrenceId, DateTimeOffset acceptedAtUtc, SchedulerOneShotStatus status)
    {
        if (entryId == Guid.Empty || occurrenceId == Guid.Empty
            || time.Ticks % TimeSpan.TicksPerMinute != 0 || !Enum.IsDefined(status))
        {
            throw new InvalidDataException("一回予約の状態が不正です。");
        }

        EntryId = entryId;
        Date = date;
        Time = time;
        OccurrenceId = occurrenceId;
        AcceptedAtUtc = acceptedAtUtc.ToUniversalTime();
        Status = status;
    }

    internal Guid EntryId
    {
        get;
    }

    internal DateOnly Date
    {
        get;
    }

    internal TimeOnly Time
    {
        get;
    }

    internal Guid OccurrenceId
    {
        get;
    }

    internal DateTimeOffset AcceptedAtUtc
    {
        get;
    }

    internal SchedulerOneShotStatus Status
    {
        get;
    }

    internal bool AppliesTo(SchedulerEntry? entry) => entry is not null
        && entry.Id == EntryId && entry.Condition is SchedulerOneShotCondition condition
        && condition.Date == Date && condition.Time == Time;

    internal SchedulerOneShotState WithStatus(SchedulerOneShotStatus status)
    {
        bool valid = Status switch
        {
            SchedulerOneShotStatus.Accepted => status is SchedulerOneShotStatus.Started
                or SchedulerOneShotStatus.Failed or SchedulerOneShotStatus.Cancelled
                or SchedulerOneShotStatus.Unknown,
            SchedulerOneShotStatus.Started => status is SchedulerOneShotStatus.Succeeded
                or SchedulerOneShotStatus.Failed or SchedulerOneShotStatus.Cancelled
                or SchedulerOneShotStatus.Unknown,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidOperationException("一回予約の状態をこの順序では変更できません。");
        }

        return new SchedulerOneShotState(EntryId, Date, Time,
            OccurrenceId, AcceptedAtUtc, status);
    }
}
