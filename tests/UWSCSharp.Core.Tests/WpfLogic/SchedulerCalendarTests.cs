using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerCalendarTests
{
    [Theory]
    [InlineData(0, 56, true)]
    [InlineData(1, 0, false)]
    [InlineData(1, 3, true)]
    [InlineData(23, 55, true)]
    [InlineData(0, 0, true)]
    public void MinuteIntervalsUseDailyMidnight(int hour, int minute, bool expected)
    {
        AssertMatch(new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 7),
            new DateTime(2026, 10, 31, hour, minute, 0), expected);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(7, 0, true)]
    [InlineData(7, 1, false)]
    [InlineData(8, 0, false)]
    [InlineData(21, 0, true)]
    public void HourIntervalsRequireMinuteZero(int hour, int minute, bool expected)
    {
        AssertMatch(new SchedulerIntervalCondition(SchedulerIntervalUnit.Hour, 7),
            new DateTime(2026, 10, 31, hour, minute, 0), expected);
    }

    [Fact]
    public void CalendarDaysHandleLeapDayAndSundayZero()
    {
        var condition = new SchedulerTimeCondition(new TimeOnly(12, 30),
            SchedulerDayKind.MonthDays, [29, 31]);
        AssertMatch(condition, new DateTime(2028, 2, 29, 12, 30, 0), true);
        AssertMatch(condition, new DateTime(2027, 2, 28, 12, 30, 0), false);
        AssertMatch(condition, new DateTime(2028, 2, 29, 12, 31, 0), false);
        AssertMatch(new SchedulerTimeCondition(new TimeOnly(0, 0),
            SchedulerDayKind.Weekdays, [0]), new DateTime(2026, 11, 1), true);
    }

    [Fact]
    public void OneShotMatchesWithoutConsumingAndHonorsConsumedInput()
    {
        var time = new DateTime(2028, 2, 29, 12, 30, 0);
        SchedulerEntry entry = SchedulerSettingsTests.Entry(new SchedulerOneShotCondition(
            DateOnly.FromDateTime(time), TimeOnly.FromDateTime(time)));
        var calendar = new SchedulerCalendar(new SchedulerSettings([entry]));
        Assert.Equal(SchedulerMatchKind.Match, calendar.Evaluate(entry.Id, time).Kind);
        Assert.Equal(SchedulerMatchKind.Match, calendar.Evaluate(entry.Id, time).Kind);
        Assert.Equal(SchedulerMatchKind.NoMatch, calendar.Evaluate(entry.Id, time, true).Kind);
        Assert.Equal(SchedulerMatchKind.NoMatch, calendar.Evaluate(entry.Id, time.AddMinutes(1)).Kind);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("2", false)]
    [InlineData("-1", false)]
    [InlineData("0.5", false)]
    [InlineData("1.00000001", false)]
    [InlineData("(YY=1999) AND (MM=12) AND (DD=31) AND (HH=23) AND (NN=59) AND (WW=5) AND (YMDNN=-1)", true)]
    public void ExpressionsUseCalendarVariablesAndExactOne(string source, bool expected)
    {
        AssertMatch(new SchedulerExpressionCondition(source),
            new DateTime(1999, 12, 31, 23, 59, 0), expected);
    }

    [Fact]
    public void DomainFailureDoesNotChangeOtherRowsOrLaterEvaluations()
    {
        SchedulerEntry expression = SchedulerSettingsTests.Entry(
            new SchedulerExpressionCondition("1/NN"));
        SchedulerEntry interval = SchedulerSettingsTests.Entry(
            new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1));
        var calendar = new SchedulerCalendar(new SchedulerSettings([expression, interval]));
        var time = new DateTime(2026, 1, 1);
        Assert.Equal(SchedulerMatchKind.Error, calendar.Evaluate(expression.Id, time).Kind);
        Assert.Equal(SchedulerMatchKind.Match, calendar.Evaluate(interval.Id, time).Kind);
        Assert.Equal(SchedulerMatchKind.Match, calendar.Evaluate(expression.Id, time.AddMinutes(1)).Kind);
    }

    [Fact]
    public void StartupAndWindowAreExplicitlyOutsideCalendarEvaluation()
    {
        foreach (SchedulerCondition condition in new SchedulerCondition[]
        {
            new SchedulerStartupCondition(), new SchedulerWindowCondition("a", "")
        })
        {
            SchedulerEntry entry = SchedulerSettingsTests.Entry(condition);
            var calendar = new SchedulerCalendar(new SchedulerSettings([entry]));
            Assert.Equal(SchedulerMatchKind.NotApplicable,
                calendar.Evaluate(entry.Id, new DateTime(2026, 1, 1)).Kind);
        }
    }

    [Fact]
    public void RejectsInvalidActiveExpressionBeforeMonitoring()
    {
        SchedulerEntry entry = SchedulerSettingsTests.Entry(new SchedulerExpressionCondition("FOPEN(1)"));
        Assert.Throws<InvalidDataException>(() => new SchedulerCalendar(new SchedulerSettings([entry])));
        var disabled = new SchedulerEntry(entry.Id, entry.Title, entry.Path, entry.Arguments,
            entry.Mode, entry.Condition, Path.GetPathRoot(entry.Path)!, false);
        var calendar = new SchedulerCalendar(new SchedulerSettings([disabled]));
        Assert.Equal(SchedulerMatchKind.NotApplicable,
            calendar.Evaluate(disabled.Id, new DateTime(2026, 1, 1)).Kind);
        Assert.Throws<ArgumentException>(() => calendar.Evaluate(disabled.Id, DateTime.UtcNow));
    }

    [Fact]
    public void ExplicitTimeZoneProjectionPreservesDstGapAndRepeatedWallMinute()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var zone = TimeZoneInfo.CreateCustomTimeZone("calendar-test", TimeSpan.FromHours(-5),
            "検査", "標準", "夏", [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), start, end)]);
        DateTime before = SchedulerCalendar.LocalMinute(
            new DateTimeOffset(2026, 3, 8, 6, 59, 59, TimeSpan.Zero), zone);
        DateTime after = SchedulerCalendar.LocalMinute(
            new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), zone);
        Assert.Equal(new DateTime(2026, 3, 8, 1, 59, 0), before);
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0), after);
        Assert.Equal(SchedulerCalendar.LocalMinute(
                new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), zone),
            SchedulerCalendar.LocalMinute(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), zone));
        Assert.Equal(DateTimeKind.Unspecified, after.Kind);
    }

    private static void AssertMatch(SchedulerCondition condition, DateTime time, bool expected)
    {
        SchedulerEntry entry = SchedulerSettingsTests.Entry(condition);
        var calendar = new SchedulerCalendar(new SchedulerSettings([entry]));
        Assert.Equal(expected ? SchedulerMatchKind.Match : SchedulerMatchKind.NoMatch,
            calendar.Evaluate(entry.Id, time).Kind);
    }
}
