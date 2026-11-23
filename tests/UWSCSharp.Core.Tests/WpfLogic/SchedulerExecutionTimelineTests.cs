using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 時刻データだけを扱い、時計や画面・入力を操作しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerExecutionTimelineTests
{
    private static readonly DateTimeOffset Accepted = new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

    [Fact]
    public void PreparationDoesNotInventStartOrCompletion()
    {
        var timeline = new SchedulerExecutionTimeline(Accepted);
        Assert.Equal(Accepted, timeline.AcceptedAt);
        Assert.Null(timeline.StartedAt);
        Assert.Null(timeline.EndedAt);
        Assert.Null(timeline.ResultObservedAt);
    }

    [Fact]
    public void RepeatedAndLateObservationsKeepTheFirstConfirmedTimes()
    {
        var timeline = new SchedulerExecutionTimeline(Accepted);
        timeline.ObserveStarted(Accepted.AddSeconds(1));
        timeline.ObserveStarted(Accepted.AddSeconds(2));
        timeline.ObserveResult(Accepted.AddSeconds(3), endedConfirmed: true);
        timeline.ObserveStarted(Accepted.AddSeconds(4));
        timeline.ObserveResult(Accepted.AddSeconds(5), endedConfirmed: true);
        Assert.Equal(Accepted.AddSeconds(1), timeline.StartedAt);
        Assert.Equal(Accepted.AddSeconds(3), timeline.EndedAt);
    }

    [Fact]
    public void CompletionWithoutStartRejectsLateStartObservation()
    {
        var timeline = new SchedulerExecutionTimeline(Accepted);
        timeline.ObserveResult(Accepted.AddSeconds(1), endedConfirmed: true);
        timeline.ObserveStarted(Accepted.AddSeconds(2));
        Assert.Null(timeline.StartedAt);
        Assert.Equal(Accepted.AddSeconds(1), timeline.EndedAt);
    }

    [Fact]
    public void ClockRollbackIsStoredWithoutChangingAnyTime()
    {
        var timeline = new SchedulerExecutionTimeline(Accepted);
        timeline.ObserveStarted(Accepted.AddSeconds(-1));
        timeline.ObserveResult(Accepted.AddSeconds(-2), endedConfirmed: true);
        Assert.Equal(Accepted, timeline.AcceptedAt);
        Assert.Equal(Accepted.AddSeconds(-1), timeline.StartedAt);
        Assert.Equal(Accepted.AddSeconds(-2), timeline.EndedAt);
    }

    [Fact]
    public void UnknownProcessResultDoesNotInventEndAndBlocksLateStart()
    {
        var timeline = new SchedulerExecutionTimeline(Accepted);
        timeline.ObserveResult(Accepted.AddSeconds(1), endedConfirmed: false);
        timeline.ObserveStarted(Accepted.AddSeconds(2));
        Assert.Equal(Accepted.AddSeconds(1), timeline.ResultObservedAt);
        Assert.Null(timeline.EndedAt);
        Assert.Null(timeline.StartedAt);
        timeline.ObserveResult(Accepted.AddSeconds(3), endedConfirmed: true);
        Assert.Equal(Accepted.AddSeconds(1), timeline.ResultObservedAt);
        Assert.Equal(Accepted.AddSeconds(3), timeline.EndedAt);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(-5)]
    public void DifferentOffsetsPreserveTheSameInstantsInUtc(int offset)
    {
        DateTimeOffset local = Accepted.ToOffset(TimeSpan.FromHours(offset));
        var timeline = new SchedulerExecutionTimeline(local);
        timeline.ObserveStarted(local.AddSeconds(1));
        timeline.ObserveResult(local.AddSeconds(2), endedConfirmed: true);
        Assert.Equal(TimeSpan.Zero, timeline.AcceptedAt.Offset);
        Assert.Equal(TimeSpan.Zero, timeline.StartedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, timeline.EndedAt!.Value.Offset);
        Assert.Equal(Accepted, timeline.AcceptedAt);
        Assert.Equal(Accepted.AddSeconds(1), timeline.StartedAt);
        Assert.Equal(Accepted.AddSeconds(2), timeline.EndedAt);
    }
}
