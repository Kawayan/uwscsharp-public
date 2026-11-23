using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWindowMatcherTests
{
    private static readonly DateTimeOffset Now =
        new(2027, 1, 2, 3, 4, 0, TimeSpan.FromHours(9));
    private readonly SchedulerWindowMatcher matcher = new();
    private readonly Guid generation = Guid.NewGuid();
    private readonly List<SchedulerWindowTrigger> accepted = [];

    [Fact]
    public void KnownFirstWindowDoesNotHideNewCandidateOrChangeDeterministicOrder()
    {
        Guid epoch = Configure(Entry());
        Assert.Equal(1, Observe(epoch, Window(8)));
        Assert.Equal(1, Observe(epoch, Window(8), Window(9), Window(2)));
        Assert.Equal(1, Observe(epoch, Window(9), Window(2), Window(8)));
        Assert.Equal(0, Observe(epoch, Window(2), Window(8), Window(9)));
        Assert.Equal(new ulong[] { 8, 2, 9 }, accepted.Select(item => item.Target.Handle));
    }

    [Fact]
    public void IndividualDisappearanceRearmsWithoutWaitingForEveryWindowToClose()
    {
        Guid epoch = Configure(Entry());
        Observe(epoch, Window(1), Window(2));
        Observe(epoch, Window(1), Window(2));
        Assert.Equal(0, Observe(epoch, Window(2)));
        Assert.Equal(1, Observe(epoch, Window(1), Window(2)));
        Assert.Equal(3, accepted.Count);
    }

    [Fact]
    public void TitleMismatchRearmsTheSameIdentity()
    {
        Guid epoch = Configure(Entry());
        Observe(epoch, Window(1));
        Assert.Equal(0, Observe(epoch, Window(1, "other")));
        Assert.Equal(1, Observe(epoch, Window(1)));
    }

    [Fact]
    public void FullQueueDoesNotConsumeCandidateAndAbsentCandidateIsNotCaughtUp()
    {
        Guid epoch = Configure(Entry());
        int calls = 0;
        Assert.Equal(0, matcher.Observe(epoch, Snapshot(Window(2), Window(1)), (_, trigger) =>
        {
            calls++;
            Assert.Equal(1UL, trigger.Target.Handle);
            return false;
        }));
        Assert.Equal(1, calls);
        Assert.Equal(1, Observe(epoch, Window(2)));
        Assert.Equal(2UL, Assert.Single(accepted).Target.Handle);
    }

    [Fact]
    public void RejectedCandidateCanBeAcceptedOnNextObservation()
    {
        Guid epoch = Configure(Entry());
        matcher.Observe(epoch, Snapshot(Window(1)), (_, _) => false);
        Assert.Equal(1, Observe(epoch, Window(1)));
        Assert.Equal(0, Observe(epoch, Window(1)));
    }

    [Fact]
    public void FailedObservationDoesNotEraseHistoryButCompleteEmptyObservationDoes()
    {
        Guid epoch = Configure(Entry());
        Observe(epoch, Window(1));
        Assert.Equal(0, matcher.Observe(epoch,
            SchedulerWindowSnapshot.Failed(Now, new IOException("列挙失敗")),
            (_, _) => throw new InvalidOperationException("受付してはいけません。")));
        Assert.Equal(0, Observe(epoch, Window(1)));
        Observe(epoch);
        Assert.Equal(1, Observe(epoch, Window(1)));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void ChangedOwnerIsNewEvenWhenHandleIsReused(uint process, uint thread)
    {
        Guid epoch = Configure(Entry());
        Observe(epoch, Window(1));
        Assert.Equal(1, Observe(epoch,
            new SchedulerObservedWindow(new(1, process, thread), "対象", "Main")));
    }

    [Fact]
    public void MoveAndMetadataChangeKeepHistoryAndUseCurrentDefinition()
    {
        SchedulerEntry first = Entry();
        SchedulerEntry second = Entry();
        Guid epoch = Configure(first, second);
        Assert.Equal(2, Observe(epoch, Window(1)));
        var changed = new SchedulerEntry(first.Id, "変更後", first.Path,
            ["新しい引数"], first.Mode, first.Condition, Path.GetTempPath());
        epoch = Configure(second, changed);
        Assert.Equal(0, Observe(epoch, Window(1)));
        var order = new List<Guid>();
        Assert.Equal(2, matcher.Observe(epoch, Snapshot(Window(1), Window(2)), (entry, _) =>
        {
            order.Add(entry.Id);
            if (entry.Id == first.Id)
            {
                Assert.Same(changed, entry);
            }
            return true;
        }));
        Assert.Equal(new[] { second.Id, first.Id }, order);
    }

    [Fact]
    public void ConditionChangeAndDisableClearOnlyRelevantHistory()
    {
        SchedulerEntry first = Entry();
        SchedulerEntry second = Entry();
        Guid epoch = Configure(first, second);
        Observe(epoch, Window(1));
        var changed = new SchedulerEntry(first.Id, first.Title, first.Path,
            first.Arguments, first.Mode, new SchedulerWindowCondition("対", "Main"), Path.GetTempPath());
        epoch = Configure(changed, second);
        Assert.Equal(1, Observe(epoch, Window(1)));
        epoch = matcher.Configure(generation, new SchedulerSettings([changed, second], 40));
        Assert.Equal(0, Observe(epoch, Window(1)));
        epoch = Configure(changed, second);
        Assert.Equal(2, Observe(epoch, Window(1)));
    }

    [Fact]
    public void RemovedRowAndNewOwnerStartWithCurrentWindowsAndIgnoreLateResults()
    {
        SchedulerEntry entry = Entry();
        Guid oldEpoch = Configure(entry);
        Observe(oldEpoch, Window(1));
        Guid epoch = Configure();
        Assert.Equal(0, Observe(oldEpoch, Window(2)));
        Assert.Equal(0, Observe(epoch, Window(1)));
        epoch = Configure(entry);
        Assert.Equal(1, Observe(epoch, Window(1)));
        Guid newEpoch = matcher.Configure(Guid.NewGuid(), Settings(entry));
        Assert.Equal(0, Observe(epoch, Window(2)));
        Assert.Equal(1, Observe(newEpoch, Window(1)));
    }

    [Theory]
    [InlineData("対", "", "対象", "Any", 1)]
    [InlineData("", "MAIN", "", "main", 1)]
    [InlineData("対象", "Main", "対象", "MainChild", 0)]
    [InlineData("abc", "Main", "ABC対象", "main", 1)]
    [InlineData("対象", "Main", "other", "Main", 0)]
    public void MatchingUsesPartialTitleAndExactClass(string title, string className,
        string actualTitle, string actualClass, int expected)
    {
        Guid epoch = Configure(Entry(title, className));
        Assert.Equal(expected, Observe(epoch,
            new SchedulerObservedWindow(new(1, 1, 1), actualTitle, actualClass)));
    }

    [Fact]
    public void DisabledAndNonWindowRowsNeverProduceCandidates()
    {
        SchedulerEntry entry = Entry();
        var disabled = new SchedulerEntry(entry.Id, entry.Title, entry.Path, entry.Arguments,
            entry.Mode, entry.Condition, Path.GetTempPath(), enabled: false);
        Guid epoch = Configure(disabled, SchedulerSettingsTests.Entry());
        Assert.Equal(0, Observe(epoch, Window(1)));
    }

    [Fact]
    public void SnapshotCopiesInputAndPreservesUtcObservationTime()
    {
        SchedulerObservedWindow[] source = [Window(1)];
        SchedulerWindowSnapshot snapshot = Snapshot(source);
        source[0] = Window(2);
        Assert.Equal(1UL, Assert.Single(snapshot.Windows).Target.Handle);
        Guid epoch = Configure(Entry());
        Assert.Equal(1, matcher.Observe(epoch, snapshot, Accept));
        Assert.Equal(Now.ToUniversalTime(), Assert.Single(accepted).ObservedAt);
        Assert.Equal(TimeSpan.Zero, accepted[0].ObservedAt.Offset);
        Assert.Throws<ArgumentException>(() => matcher.Configure(Guid.Empty, Settings(Entry())));
        Assert.Equal(0, Observe(Guid.Empty, Window(2)));
    }

    [Fact]
    public void SnapshotRejectsExcessAndDuplicateHandlesWithoutKeepingUnboundedInput()
    {
        Assert.Equal(4096, Snapshot([.. Enumerable.Range(1, 4096).Select(id => Window((ulong)id))]).Windows.Count);
        int reads = 0;
        Assert.Throws<InvalidDataException>(() => SchedulerWindowSnapshot.Complete(Now, Unlimited()));
        Assert.Equal(4097, reads);
        Assert.Throws<InvalidDataException>(() => Snapshot(Window(1),
            new(new(1, 2, 2), "対象", "Main")));

        IEnumerable<SchedulerObservedWindow> Unlimited()
        {
            while (true)
            {
                reads++;
                yield return Window((ulong)reads);
            }
        }
    }

    [Fact]
    public void SnapshotChecksTitleClassAndIdentityBounds()
    {
        Snapshot(Window(1, new string('字', 32768), new string('C', 512)));
        Assert.Throws<InvalidDataException>(() => Snapshot(Window(1, new string('字', 32769))));
        Assert.Throws<InvalidDataException>(() => Snapshot(Window(1, "対象", new string('C', 513))));
        Assert.Throws<InvalidDataException>(() => Snapshot(Window(1, "対象\0後")));
        Assert.Throws<InvalidDataException>(() =>
            Snapshot(new SchedulerObservedWindow(default, "対象", "Main")));
    }

    private Guid Configure(params SchedulerEntry[] entries) =>
        matcher.Configure(generation, Settings(entries));

    private static SchedulerSettings Settings(params SchedulerEntry[] entries) =>
        new(entries, 40, enabled: true);

    private int Observe(Guid epoch, params SchedulerObservedWindow[] windows) =>
        matcher.Observe(epoch, Snapshot(windows), Accept);

    private bool Accept(SchedulerEntry entry, SchedulerWindowTrigger trigger)
    {
        accepted.Add(trigger);
        return true;
    }

    private static SchedulerWindowSnapshot Snapshot(params SchedulerObservedWindow[] windows) =>
        SchedulerWindowSnapshot.Complete(Now, windows);

    private static SchedulerObservedWindow Window(ulong handle,
        string title = "対象", string className = "Main") => new(new(handle, 1, 1), title, className);

    private static SchedulerEntry Entry(string title = "対象", string className = "Main") =>
        SchedulerSettingsTests.Entry(new SchedulerWindowCondition(title, className));
}
