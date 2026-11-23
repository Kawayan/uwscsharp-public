using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerPendingQueueTests
{
    [Fact]
    public void PreservesFifoAndLimitsEachRowToOneRunningAndOnePending()
    {
        var queue = new SchedulerPendingQueue();
        SchedulerEntry entry = SchedulerSettingsTests.Entry();
        SchedulerExecutionRequest first = Request(entry);
        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(Request(entry)));
        Assert.Same(first, queue.TryTake());
        SchedulerExecutionRequest second = Request(entry);
        Assert.True(queue.TryEnqueue(second));
        Assert.Null(queue.TryTake());
        Assert.False(queue.Complete(Guid.NewGuid()));
        Assert.True(queue.Complete(first.OccurrenceId));
        Assert.Same(second, queue.TryTake());
        Assert.False(queue.Complete(first.OccurrenceId));
        Assert.Equal(1, queue.RunningCount);
        Assert.True(queue.Complete(second.OccurrenceId));
    }

    [Fact]
    public void SeparateRowsCanPassBusySameProcessButSameProcessCannotOverlap()
    {
        var queue = new SchedulerPendingQueue();
        SchedulerExecutionRequest a = Request(WithMode(SchedulerSettingsTests.Entry(), SchedulerExecutionMode.SameProcess));
        SchedulerExecutionRequest b = Request(WithMode(SchedulerSettingsTests.Entry(), SchedulerExecutionMode.SameProcess));
        SchedulerExecutionRequest c = Request(SchedulerSettingsTests.Entry());
        queue.TryEnqueue(a);
        queue.TryEnqueue(b);
        queue.TryEnqueue(c);
        Assert.Null(queue.TryTake(true, false));
        Assert.Same(c, queue.TryTake(true));
        Assert.Same(a, queue.TryTake());
        Assert.Null(queue.TryTake());
        Assert.True(queue.Complete(a.OccurrenceId));
        Assert.Same(b, queue.TryTake());
    }

    [Fact]
    public void MovingAndRenamingKeepPendingOrderAndRefreshOnlyPresentation()
    {
        var queue = new SchedulerPendingQueue();
        SchedulerEntry a = SchedulerSettingsTests.Entry();
        SchedulerEntry b = SchedulerSettingsTests.Entry();
        SchedulerExecutionRequest first = Request(a);
        SchedulerExecutionRequest second = Request(b);
        queue.TryEnqueue(first);
        queue.TryEnqueue(second);
        var renamed = new SchedulerEntry(a.Id, "変更後", a.Path, a.Arguments,
            a.Mode, a.Condition, Path.GetPathRoot(a.Path)!);
        Assert.Empty(queue.Reconcile(new SchedulerSettings([b, renamed], enabled: true)));
        SchedulerExecutionRequest taken = Assert.IsType<SchedulerExecutionRequest>(queue.TryTake());
        Assert.Equal(first.OccurrenceId, taken.OccurrenceId);
        Assert.Same(renamed, taken.Entry);
        Assert.Same(second.Entry, queue.TryTake()!.Entry);
    }

    [Fact]
    public void DefinitionChangeAndDisableCancelOnlyPending()
    {
        var queue = new SchedulerPendingQueue();
        SchedulerEntry a = SchedulerSettingsTests.Entry();
        SchedulerEntry b = SchedulerSettingsTests.Entry();
        SchedulerExecutionRequest running = Request(a);
        queue.TryEnqueue(running);
        queue.TryTake();
        SchedulerExecutionRequest pending = Request(a);
        queue.TryEnqueue(pending);
        queue.TryEnqueue(Request(b));
        var changed = new SchedulerEntry(a.Id, a.Title, a.Path, ["changed"],
            a.Mode, a.Condition, Path.GetPathRoot(a.Path)!);
        Assert.Equal(pending.OccurrenceId,
            Assert.Single(queue.Reconcile(new SchedulerSettings([changed, b], enabled: true))).OccurrenceId);
        Assert.Single(queue.CancelAll());
        Assert.Equal(1, queue.RunningCount);
        Assert.True(queue.Complete(running.OccurrenceId));
        queue.TryEnqueue(Request(changed));
        Assert.Single(queue.Reconcile(new SchedulerSettings([changed])));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void BoundsPendingCapacityAndRejectsRepeatedOccurrenceIdentity()
    {
        var queue = new SchedulerPendingQueue();
        SchedulerExecutionRequest first = Request(SchedulerSettingsTests.Entry());
        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(first with
        {
            Entry = SchedulerSettingsTests.Entry()
        }));
        for (int index = 1; index < 400; index++)
        {
            Assert.True(queue.TryEnqueue(Request(SchedulerSettingsTests.Entry())));
        }

        Assert.False(queue.TryEnqueue(Request(SchedulerSettingsTests.Entry())));
        Assert.Equal(400, queue.PendingCount);
        Assert.Equal(400, queue.CancelAll().Count);
        Assert.Empty(queue.CancelAll());
        Assert.Throws<ArgumentException>(() => queue.TryEnqueue(first with { OccurrenceId = Guid.Empty }));
    }

    [Fact]
    public void EquivalentDaySetsDoNotCancelButChangedConditionsDo()
    {
        SchedulerEntry original = SchedulerSettingsTests.Entry(new SchedulerTimeCondition(
            new TimeOnly(1, 0), SchedulerDayKind.Weekdays, [1, 2]));
        var reordered = new SchedulerEntry(original.Id, original.Title, original.Path,
            original.Arguments, original.Mode, new SchedulerTimeCondition(
                new TimeOnly(1, 0), SchedulerDayKind.Weekdays, [2, 1]), Path.GetPathRoot(original.Path)!);
        Assert.True(SchedulerExecutionDefinition.Same(original, reordered));
        var changed = new SchedulerEntry(original.Id, original.Title, original.Path,
            original.Arguments, original.Mode, new SchedulerTimeCondition(
                new TimeOnly(1, 0), SchedulerDayKind.Weekdays, [1]), Path.GetPathRoot(original.Path)!);
        Assert.False(SchedulerExecutionDefinition.Same(original, changed));
    }

    private static SchedulerExecutionRequest Request(SchedulerEntry entry) =>
        new(Guid.NewGuid(), entry, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static SchedulerEntry WithMode(SchedulerEntry entry, SchedulerExecutionMode mode) =>
        new(entry.Id, entry.Title, entry.Path, entry.Arguments, mode,
            entry.Condition, Path.GetPathRoot(entry.Path)!);
}
