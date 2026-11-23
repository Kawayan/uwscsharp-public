using System.IO;

namespace UWSCSharp.Wpf;

internal enum SchedulerIntervalUnit
{
    Minute,
    Hour
}

internal enum SchedulerDayKind
{
    EveryDay,
    Weekdays,
    MonthDays
}

/// <summary>
/// 保存する条件を表し、時計の評価や処理の起動は行いません。
/// </summary>
internal abstract class SchedulerCondition
{
    private protected SchedulerCondition()
    {
    }
}

internal sealed class SchedulerStartupCondition : SchedulerCondition
{
}

internal sealed class SchedulerIntervalCondition : SchedulerCondition
{
    internal SchedulerIntervalCondition(SchedulerIntervalUnit unit, int value)
    {
        if (!Enum.IsDefined(unit) || value < 1
            || value > (unit == SchedulerIntervalUnit.Minute ? 60 : 24))
        {
            throw new InvalidDataException("予約の間隔が範囲外です。");
        }

        Unit = unit;
        Value = value;
    }

    internal SchedulerIntervalUnit Unit
    {
        get;
    }

    internal int Value
    {
        get;
    }
}

internal sealed class SchedulerTimeCondition : SchedulerCondition
{
    internal SchedulerTimeCondition(TimeOnly time, SchedulerDayKind dayKind,
        IEnumerable<int> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (time.Ticks % TimeSpan.TicksPerMinute != 0 || !Enum.IsDefined(dayKind))
        {
            throw new InvalidDataException("予約の時刻または日付指定が不正です。");
        }

        var copied = new List<int>();
        var unique = new HashSet<int>();
        foreach (int day in days)
        {
            bool valid = dayKind switch
            {
                SchedulerDayKind.Weekdays => day is >= 0 and <= 6,
                SchedulerDayKind.MonthDays => day is >= 1 and <= 31,
                _ => false
            };
            if (!valid || !unique.Add(day))
            {
                throw new InvalidDataException("予約の日付指定が範囲外か重複しています。");
            }

            copied.Add(day);
        }

        if (dayKind != SchedulerDayKind.EveryDay && copied.Count == 0)
        {
            throw new InvalidDataException("曜日または日付を1つ以上指定してください。");
        }

        Time = time;
        DayKind = dayKind;
        Days = copied.AsReadOnly();
    }

    internal TimeOnly Time
    {
        get;
    }

    internal SchedulerDayKind DayKind
    {
        get;
    }

    internal IReadOnlyList<int> Days
    {
        get;
    }
}

internal sealed class SchedulerExpressionCondition : SchedulerCondition
{
    internal SchedulerExpressionCondition(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        SchedulerValidation.Text(source);
        Source = source;
    }

    internal string Source
    {
        get;
    }
}

internal sealed class SchedulerOneShotCondition : SchedulerCondition
{
    internal SchedulerOneShotCondition(DateOnly date, TimeOnly time)
    {
        if (time.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new InvalidDataException("一回予約の時刻は分単位で指定してください。");
        }

        Date = date;
        Time = time;
    }

    internal DateOnly Date
    {
        get;
    }

    internal TimeOnly Time
    {
        get;
    }
}

internal sealed class SchedulerWindowCondition : SchedulerCondition
{
    internal SchedulerWindowCondition(string title, string className)
    {
        SchedulerValidation.Text(title);
        SchedulerValidation.Text(className);
        if (title.Length == 0 && className.Length == 0)
        {
            throw new InvalidDataException("窓のタイトルかクラス名を指定してください。");
        }

        Title = title;
        ClassName = className;
    }

    internal string Title
    {
        get;
    }

    internal string ClassName
    {
        get;
    }
}
