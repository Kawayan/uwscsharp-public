using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWakePlannerTests
{
    private static readonly DateTimeOffset Now = new(2027, 2, 1, 10, 7, 25, TimeSpan.Zero);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DisabledOptionsDoNotPlanWake(bool enabled, bool wake)
    {
        var settings = new SchedulerSettings([Entry(new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1))],
            enabled: enabled, wakeUp: wake);
        Assert.Null(SchedulerWakePlanner.Find(settings, Now, TimeZoneInfo.Utc).DueAt);
    }

    [Fact]
    public void StartupAndWindowConditionsDoNotInventWakeTimes()
    {
        Assert.Null(Plan([Entry(new SchedulerStartupCondition()),
            Entry(new SchedulerWindowCondition("title", ""))]).DueAt);
    }

    [Theory]
    [InlineData(0, 10, 10, 10)]
    [InlineData(1, 3, 12, 0)]
    public void IntervalsUseLocalMidnightOrigin(int unit, int value, int hour, int minute)
    {
        SchedulerWakePlan result = Plan([Entry(new SchedulerIntervalCondition((SchedulerIntervalUnit)unit, value))]);
        Assert.Equal(new DateTimeOffset(2027, 2, 1, hour, minute, 0, TimeSpan.Zero), result.DueAt);
        Assert.False(result.IsCheckpoint);
    }

    [Fact]
    public void IntervalResetsAtNextMidnight()
    {
        var now = new DateTimeOffset(2027, 2, 1, 23, 59, 0, TimeSpan.Zero);
        Assert.Equal(now.AddMinutes(1), Plan([Entry(new SchedulerIntervalCondition(
            SchedulerIntervalUnit.Minute, 7))], now).DueAt);
    }

    [Fact]
    public void CurrentMinuteIsNotReplayed()
    {
        SchedulerWakePlan result = Plan([Entry(new SchedulerTimeCondition(new(10, 7),
            SchedulerDayKind.EveryDay, []))]);
        Assert.Equal(new DateTimeOffset(2027, 2, 2, 10, 7, 0, TimeSpan.Zero), result.DueAt);
    }

    [Fact]
    public void WeekdayAndMonthDayRespectCalendar()
    {
        SchedulerWakePlan week = Plan([Entry(new SchedulerTimeCondition(new(8, 0),
            SchedulerDayKind.Weekdays, [(int)DayOfWeek.Monday]))]);
        Assert.Equal(new DateTimeOffset(2027, 2, 8, 8, 0, 0, TimeSpan.Zero), week.DueAt);
        SchedulerWakePlan month = Plan([Entry(new SchedulerTimeCondition(new(8, 0),
            SchedulerDayKind.MonthDays, [31]))]);
        Assert.Equal(new DateTimeOffset(2027, 3, 31, 8, 0, 0, TimeSpan.Zero), month.DueAt);
    }

    [Fact]
    public void FarFutureOneShotDoesNotRequireMinuteScan()
    {
        SchedulerWakePlan plan = Plan([Entry(new SchedulerOneShotCondition(new(2099, 12, 31), new(23, 59)))]);
        Assert.Equal(new DateTimeOffset(2099, 12, 31, 23, 59, 0, TimeSpan.Zero), plan.DueAt);
        Assert.False(plan.IsCheckpoint);
    }

    [Fact]
    public void ConsumedOneShotIsExcludedWithoutChangingSettings()
    {
        var condition = new SchedulerOneShotCondition(new(2027, 2, 2), new(12, 0));
        SchedulerEntry entry = Entry(condition);
        var state = new SchedulerOneShotState(entry.Id, condition.Date, condition.Time,
            Guid.NewGuid(), Now, SchedulerOneShotStatus.Accepted);
        var settings = new SchedulerSettings([entry], enabled: true, wakeUp: true, oneShotStates: [state]);
        Assert.Null(SchedulerWakePlanner.Find(settings, Now, TimeZoneInfo.Utc).DueAt);
        Assert.Same(state, Assert.Single(settings.OneShotStates));
    }

    [Fact]
    public void EarlierRowsDoNotHideEarlierTimesInLaterRows()
    {
        SchedulerWakePlan plan = Plan([Entry(new SchedulerOneShotCondition(new(2099, 1, 1), new(0, 0))),
            Entry(new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1))]);
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 10, 8, 0, TimeSpan.Zero), plan.DueAt);
    }

    [Fact]
    public void BackwardClockSkipsObservedLocalMinutes()
    {
        var watermark = new DateTime(2027, 2, 1, 12, 0, 0, DateTimeKind.Unspecified);
        SchedulerWakePlan result = Plan([Entry(new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1))],
            watermark: watermark);
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 12, 1, 0, TimeSpan.Zero), result.DueAt);
    }

    [Fact]
    public void MissingDstTimeIsSkipped()
    {
        SchedulerWakePlan plan = Plan([Entry(new SchedulerTimeCondition(new(2, 30), SchedulerDayKind.EveryDay, []))],
            new(2027, 3, 28, 1, 0, 0, TimeSpan.Zero), DstZone());
        Assert.Equal(new DateTimeOffset(2027, 3, 29, 1, 30, 0, TimeSpan.Zero), plan.DueAt);
    }

    [Fact]
    public void InvalidMonthDayBeyondSearchRangeProducesCheckpoint()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("month-gap", TimeSpan.Zero,
            "month-gap", "standard", "daylight",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2027, 1, 1), new(2027, 12, 31),
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new(1, 1, 1, 2, 0, 0), 3, 31),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new(1, 1, 1, 3, 0, 0), 10, 31))]);
        SchedulerEntry entry = Entry(new SchedulerTimeCondition(new(2, 30), SchedulerDayKind.MonthDays, [31]));
        SchedulerWakePlan first = Plan([entry], zone: zone);
        Assert.True(first.IsCheckpoint);
        Assert.True(first.DueAt > Now && first.DueAt < new DateTimeOffset(2027, 5, 31, 1, 30, 0, TimeSpan.Zero));
        SchedulerWakePlan next = Plan([entry], first.DueAt, zone);
        Assert.False(next.IsCheckpoint);
        Assert.Equal(new DateTimeOffset(2027, 5, 31, 1, 30, 0, TimeSpan.Zero), next.DueAt);
    }

    [Fact]
    public void FixedOffsetZoneReturnsUtc()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("wake-plus-nine", TimeSpan.FromHours(9),
            "wake-plus-nine", "wake-plus-nine");
        SchedulerWakePlan plan = Plan([Entry(new SchedulerTimeCondition(new(20, 0), SchedulerDayKind.EveryDay, []))],
            zone: zone);
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 11, 0, 0, TimeSpan.Zero), plan.DueAt);
        Assert.Equal(TimeSpan.Zero, plan.DueAt!.Value.Offset);
    }

    [Fact]
    public void InvalidWatermarkIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Plan([], watermark: DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => Plan([], watermark: new(2027, 2, 1, 10, 0, 1)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    public void AmbiguousTimeSelectsEarliestFutureUtc(int nowHour, int dueHour)
    {
        SchedulerWakePlan plan = Plan([Entry(new SchedulerTimeCondition(new(2, 30), SchedulerDayKind.EveryDay, []))],
            new(2027, 10, 31, nowHour, 0, 0, TimeSpan.Zero), DstZone());
        Assert.Equal(new DateTimeOffset(2027, 10, 31, dueHour, 30, 0, TimeSpan.Zero), plan.DueAt);
    }

    [Fact]
    public void ObservedFoldIsNotPlannedAgain()
    {
        SchedulerWakePlan plan = Plan([Entry(new SchedulerTimeCondition(new(2, 30), SchedulerDayKind.EveryDay, []))],
            new(2027, 10, 31, 2, 0, 0, TimeSpan.Zero), DstZone(),
            new(2027, 10, 31, 2, 50, 0, DateTimeKind.Unspecified));
        Assert.Equal(new DateTimeOffset(2027, 11, 1, 2, 30, 0, TimeSpan.Zero), plan.DueAt);
    }

    [Fact]
    public void ExpressionSharesCalendarVariablesAndRequiresExactlyOne()
    {
        SchedulerWakePlan plan = Plan([Entry(new SchedulerExpressionCondition("NN=10"))]);
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 10, 10, 0, TimeSpan.Zero), plan.DueAt);
        Assert.False(plan.IsCheckpoint);
        SchedulerWakePlan nonBoolean = Plan([Entry(new SchedulerExpressionCondition("2"))]);
        Assert.True(nonBoolean.IsCheckpoint);
    }

    [Fact]
    public void FiniteExpressionHorizonProducesCheckpointInsteadOfNoMatchForever()
    {
        SchedulerWakePlan plan = Plan([Entry(new SchedulerExpressionCondition("0"))]);
        Assert.True(plan.IsCheckpoint);
        Assert.Equal(new DateTimeOffset(2027, 2, 2, 10, 8, 0, TimeSpan.Zero), plan.DueAt);
        Assert.NotNull(plan.Diagnostic);
    }

    [Fact]
    public void EvaluationBudgetReturnsFirstPartiallyUncheckedMinute()
    {
        SchedulerEntry[] entries = [.. Enumerable.Range(0, 400).Select(_ => Entry(new SchedulerExpressionCondition("0")))];
        SchedulerWakePlan plan = Plan(entries);
        Assert.True(plan.IsCheckpoint);
        int minutes = SchedulerWakePlanner.MaximumExpressionEvaluations / entries.Length + 1;
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 10, 7, 0, TimeSpan.Zero).AddMinutes(minutes), plan.DueAt);
    }

    [Fact]
    public void ExpressionErrorBecomesCheckpointAndDoesNotClaimExecution()
    {
        SchedulerWakePlan plan = Plan([Entry(new SchedulerExpressionCondition("1/0"))]);
        Assert.True(plan.IsCheckpoint);
        Assert.NotNull(plan.Diagnostic);
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 10, 8, 0, TimeSpan.Zero), plan.DueAt);
    }

    [Fact]
    public void EarlierExactMatchWinsOverUnfinishedExpressionSearch()
    {
        SchedulerWakePlan plan = Plan([Entry(new SchedulerExpressionCondition("0")),
            Entry(new SchedulerTimeCondition(new(10, 10), SchedulerDayKind.EveryDay, []))]);
        Assert.False(plan.IsCheckpoint);
        Assert.Equal(new DateTimeOffset(2027, 2, 1, 10, 10, 0, TimeSpan.Zero), plan.DueAt);
    }

    [Fact]
    public void UpperCalendarLimitDoesNotOverflow()
    {
        Assert.Null(Plan([Entry(new SchedulerExpressionCondition("0"))], DateTimeOffset.MaxValue).DueAt);
        Assert.Null(Plan([Entry(new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1))],
            DateTimeOffset.MaxValue).DueAt);
    }

    [Fact]
    public void CancellationDoesNotPublishPlan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SchedulerWakePlanner.Find(
            new SchedulerSettings(enabled: true, wakeUp: true), Now, TimeZoneInfo.Utc, token: cancellation.Token));
    }

    private static SchedulerEntry Entry(SchedulerCondition condition) => SchedulerSettingsTests.Entry(condition);

    private static SchedulerWakePlan Plan(SchedulerEntry[] entries, DateTimeOffset? now = null,
        TimeZoneInfo? zone = null, DateTime? watermark = null) => SchedulerWakePlanner.Find(
            new SchedulerSettings(entries, capacity: 400, enabled: true, wakeUp: true),
            now ?? Now, zone ?? TimeZoneInfo.Utc, watermark);

    private static TimeZoneInfo DstZone() => TimeZoneInfo.CreateCustomTimeZone(
        "wake-tests", TimeSpan.Zero, "wake-tests", "standard", "daylight",
        [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2027, 1, 1), new(2027, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday))]);
}
