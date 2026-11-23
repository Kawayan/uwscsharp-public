using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerMinuteGateTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartsAtNextMinuteAndDoesNotReplayStartup()
    {
        var gate = new SchedulerMinuteGate();
        Assert.Equal(SchedulerMinuteStatus.Inactive, gate.Observe(Noon, TimeZoneInfo.Utc).Status);
        Assert.True(gate.Start(Noon.AddSeconds(20), TimeZoneInfo.Utc));
        Assert.Equal(SchedulerMinuteStatus.Waiting, gate.Observe(Noon.AddSeconds(59), TimeZoneInfo.Utc).Status);
        Assert.Equal(SchedulerMinuteStatus.Due, gate.Observe(Noon.AddMinutes(1), TimeZoneInfo.Utc).Status);
        Assert.Equal(SchedulerMinuteStatus.Waiting, gate.Observe(Noon.AddMinutes(1), TimeZoneInfo.Utc).Status);
        gate.Stop();
        Assert.False(gate.Start(Noon.AddMinutes(5), TimeZoneInfo.Utc));
        Assert.Equal(SchedulerMinuteStatus.Waiting, gate.Observe(Noon.AddMinutes(5), TimeZoneInfo.Utc).Status);
        Assert.Equal(SchedulerMinuteStatus.Due, gate.Observe(Noon.AddMinutes(6), TimeZoneInfo.Utc).Status);
    }

    [Fact]
    public void SkipsMissedMinutesAndWaitsForRollbackToCatchUp()
    {
        var gate = new SchedulerMinuteGate();
        gate.Start(Noon, TimeZoneInfo.Utc);
        SchedulerMinuteObservation jump = gate.Observe(Noon.AddMinutes(7), TimeZoneInfo.Utc);
        Assert.Equal(SchedulerMinuteStatus.Due, jump.Status);
        Assert.Equal(Noon.DateTime.AddMinutes(7), jump.Minute);
        Assert.Equal(SchedulerMinuteStatus.ClockBehind, gate.Observe(Noon.AddMinutes(2), TimeZoneInfo.Utc).Status);
        gate.Stop();
        gate.Start(Noon.AddMinutes(3), TimeZoneInfo.Utc);
        Assert.Equal(SchedulerMinuteStatus.ClockBehind, gate.Observe(Noon.AddMinutes(6), TimeZoneInfo.Utc).Status);
        Assert.Equal(SchedulerMinuteStatus.Waiting, gate.Observe(Noon.AddMinutes(7), TimeZoneInfo.Utc).Status);
        Assert.Equal(SchedulerMinuteStatus.Due, gate.Observe(Noon.AddMinutes(8), TimeZoneInfo.Utc).Status);
    }

    [Fact]
    public void FullDateAndHourArePartOfMinuteIdentity()
    {
        var gate = new SchedulerMinuteGate();
        gate.Start(Noon, TimeZoneInfo.Utc);
        Assert.Equal(SchedulerMinuteStatus.Due, gate.Observe(Noon.AddHours(1), TimeZoneInfo.Utc).Status);
        Assert.Equal(SchedulerMinuteStatus.Due, gate.Observe(Noon.AddDays(1), TimeZoneInfo.Utc).Status);
        Assert.Equal(SchedulerMinuteStatus.Due, gate.Observe(Noon.AddYears(1), TimeZoneInfo.Utc).Status);
    }

    [Theory]
    [InlineData("UTC", 1)]
    [InlineData("different-zone", 0)]
    public void StopsForChangedRulesOrZoneIdentity(string name, int offset)
    {
        var gate = new SchedulerMinuteGate();
        gate.Start(Noon, TimeZoneInfo.Utc);
        var changed = TimeZoneInfo.CreateCustomTimeZone(name,
            TimeSpan.FromHours(offset), "検査", "検査");
        Assert.Equal(SchedulerMinuteStatus.TimeZoneChanged, gate.Observe(Noon, changed).Status);
        Assert.False(gate.IsActive);
        Assert.Equal(SchedulerMinuteStatus.Inactive, gate.Observe(Noon.AddMinutes(1), changed).Status);
        Assert.False(gate.Start(Noon.AddMinutes(1), changed));
        Assert.Equal(SchedulerMinuteStatus.Waiting, gate.Observe(Noon.AddMinutes(1), changed).Status);
        Assert.Equal(SchedulerMinuteStatus.Due, gate.Observe(Noon.AddMinutes(2), changed).Status);
    }

    [Fact]
    public void DoesNotReplayRepeatedWallMinutesDuringDstFallback()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var zone = TimeZoneInfo.CreateCustomTimeZone("gate-test", TimeSpan.FromHours(-5),
            "検査", "標準", "夏", [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), start, end)]);
        var gate = new SchedulerMinuteGate();
        var first = new DateTimeOffset(2026, 11, 1, 5, 58, 0, TimeSpan.Zero);
        gate.Start(first, zone);
        Assert.Equal(SchedulerMinuteStatus.Due, gate.Observe(first.AddMinutes(1), zone).Status);
        Assert.Equal(SchedulerMinuteStatus.ClockBehind, gate.Observe(first.AddMinutes(2), zone).Status);
        Assert.Equal(SchedulerMinuteStatus.Waiting, gate.Observe(first.AddMinutes(61), zone).Status);
        Assert.Equal(SchedulerMinuteStatus.Due, gate.Observe(first.AddMinutes(62), zone).Status);
    }
}
