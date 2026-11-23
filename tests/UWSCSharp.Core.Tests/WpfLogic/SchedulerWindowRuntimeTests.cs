using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWindowRuntimeTests
{
    private static readonly DateTimeOffset Now =
        new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

    [Fact]
    public void ReloadingSameValuesKeepsObservationAndDispatchRetainsCapturedWindow()
    {
        using var host = new Host(Entry());
        Guid epoch = host.Begin();
        host.Session.Poll(Now, TimeZoneInfo.Utc);
        Assert.Equal(epoch, host.Begin());
        byte[] before = File.ReadAllBytes(host.Path);
        Assert.Equal(1, host.Observe(epoch, 1));
        SchedulerDispatch first = host.Take();
        Assert.Equal(1UL, first.Request.WindowTrigger!.Target.Handle);
        Assert.Equal(Now, first.Request.WindowTrigger.ObservedAt);
        Assert.Equal(first.Request.ObservedAt, first.Request.WindowTrigger.ObservedAt);
        Assert.Equal(1, host.Observe(epoch, 1, 2));
        Assert.Equal(1UL, first.Request.WindowTrigger.Target.Handle);
        Assert.Null(host.TryTake());
        host.Complete(first);
        Assert.Equal(2UL, host.Take().Request.WindowTrigger!.Target.Handle);
        Assert.Equal(before, File.ReadAllBytes(host.Path));
    }

    [Fact]
    public void MetadataChangeUpdatesPendingDefinitionAndRejectsPreviousObservation()
    {
        SchedulerEntry entry = Entry();
        using var host = new Host(entry);
        Guid epoch = host.Begin();
        host.Observe(epoch, 1);
        var renamed = new SchedulerEntry(entry.Id, "新しい名前", entry.Path,
            entry.Arguments, entry.Mode, entry.Condition, System.IO.Path.GetTempPath());
        host.Save(renamed);
        Assert.Equal(0, host.Observe(epoch, 1, 2));
        Guid current = host.Begin();
        Assert.NotEqual(epoch, current);
        SchedulerDispatch dispatch = host.Take();
        Assert.Equal("新しい名前", dispatch.Request.Entry.Title);
        Assert.Equal(1UL, dispatch.Request.WindowTrigger!.Target.Handle);
        host.Complete(dispatch);
        Assert.Equal(0, host.Observe(current, 1));
    }

    [Fact]
    public void ReplacedPendingArgumentsDoNotReplayTheAlreadyAcceptedWindow()
    {
        SchedulerEntry entry = Entry();
        using var host = new Host(entry);
        Guid epoch = host.Begin();
        host.Observe(epoch, 1);
        var changed = new SchedulerEntry(entry.Id, entry.Title, entry.Path,
            ["変更"], entry.Mode, entry.Condition, System.IO.Path.GetTempPath());
        host.Save(changed);
        Guid current = host.Begin();
        Assert.Null(host.TryTake());
        Assert.Equal(0, host.Observe(current, 1));
        Assert.Equal(1, host.Observe(current, 1, 2));
        SchedulerDispatch dispatch = host.Take();
        Assert.Equal(2UL, dispatch.Request.WindowTrigger!.Target.Handle);
        Assert.Equal("変更", Assert.Single(dispatch.Request.Entry.Arguments));
    }

    [Fact]
    public void ConditionChangeDiscardsOldPendingAndUsesNewObservation()
    {
        SchedulerEntry entry = Entry();
        using var host = new Host(entry);
        Guid epoch = host.Begin();
        host.Observe(epoch, 1);
        var changed = new SchedulerEntry(entry.Id, entry.Title, entry.Path,
            entry.Arguments, entry.Mode, new SchedulerWindowCondition("別", ""),
            System.IO.Path.GetTempPath());
        host.Save(changed);
        Assert.Equal(0, host.Session.ObserveWindows(epoch, Snapshot(1)));
        Assert.Null(host.TryTake());
        Guid current = host.Begin();
        Assert.Equal(0, host.Observe(current, 1));
        Assert.Equal(1, host.Session.ObserveWindows(current,
            SchedulerWindowSnapshot.Complete(Now,
                [new SchedulerObservedWindow(new(2, 1, 1), "別", "Main")])));
    }

    [Fact]
    public void DisableDoesNotRestartAndExplicitRestartIgnoresOldObservation()
    {
        SchedulerEntry entry = Entry();
        using var host = new Host(entry);
        Guid epoch = host.Begin();
        host.Observe(epoch, 1);
        host.SaveEnabled(false, entry);
        Assert.Equal(0, host.Observe(epoch, 2));
        Assert.False(host.Session.IsActive);
        Assert.Null(host.Session.BeginWindowObservation());
        host.SaveEnabled(true, entry);
        Assert.Equal(0, host.Observe(epoch, 1));
        Assert.Null(host.TryTake());
        Assert.True(host.Session.TryStart(Now, TimeZoneInfo.Utc));
        Guid current = host.Begin();
        Assert.NotEqual(epoch, current);
        Assert.Equal(0, host.Observe(epoch, 1));
        Assert.Equal(1, host.Observe(current, 1));
    }

    [Fact]
    public void FailedSnapshotIsSeparateFromMonitorFailureAndDoesNotResetKnownWindows()
    {
        using var host = new Host(Entry());
        Guid epoch = host.Begin();
        host.Observe(epoch, 1);
        host.Complete(host.Take());
        var error = new IOException("列挙を中断");
        Assert.Equal(0, host.Session.ObserveWindows(epoch,
            SchedulerWindowSnapshot.Failed(Now, error)));
        Assert.Same(error, host.Session.WindowObservationError);
        Assert.Null(host.Session.LastError);
        Assert.True(host.Session.IsActive);
        Assert.Equal(0, host.Observe(epoch, 1));
        Assert.Null(host.Session.WindowObservationError);
        host.Observe(epoch);
        Assert.Equal(1, host.Observe(epoch, 1));
    }

    [Fact]
    public void CorruptSettingsStopWindowAdmissionWithoutOverwritingTheFile()
    {
        using var host = new Host(Entry());
        Guid epoch = host.Begin();
        File.WriteAllText(host.Path, "{");
        Assert.Equal(0, host.Observe(epoch, 1));
        Assert.False(host.Session.IsActive);
        Assert.NotNull(host.Session.LastError);
        Assert.Null(host.TryTake());
        Assert.Equal("{", File.ReadAllText(host.Path));
    }

    [Fact]
    public void FourSlotGateAndPerRowSerializationApplyToWindowRequests()
    {
        SchedulerEntry[] entries = [Entry(), Entry(), Entry(), Entry(), Entry()];
        using var host = new Host(entries);
        Guid epoch = host.Begin();
        Assert.Equal(5, host.Observe(epoch, 1));
        SchedulerDispatch[] running = [host.Take(), host.Take(), host.Take(), host.Take()];
        Assert.Null(host.TryTake());
        Assert.Equal(4, host.Observe(epoch, 1, 2));
        host.Complete(running[0]);
        SchedulerDispatch next = host.Take();
        Assert.Equal(entries[4].Id, next.Request.Entry.Id);
        Assert.Equal(1UL, next.Request.WindowTrigger!.Target.Handle);
        Assert.Null(host.TryTake());
    }

    [Fact]
    public void ClockAndWindowConditionsUseSameQueueWithoutSharingCapturedContext()
    {
        SchedulerEntry clock = SchedulerSettingsTests.Entry(
            new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1));
        using var host = new Host(Entry(), clock);
        Guid epoch = host.Begin();
        Assert.Equal(1, host.Observe(epoch, 1));
        host.Session.Poll(Now.AddMinutes(1), TimeZoneInfo.Utc);
        SchedulerDispatch window = host.Take();
        SchedulerDispatch timed = host.Take();
        Assert.NotNull(window.Request.WindowTrigger);
        Assert.Null(timed.Request.WindowTrigger);
        Assert.Equal(clock.Id, timed.Request.Entry.Id);
    }

    [Fact]
    public void QueueRejectsMissingForeignOrInvalidCapture()
    {
        var queue = new SchedulerPendingQueue();
        SchedulerEntry window = Entry();
        var trigger = new SchedulerWindowTrigger(new(1, 1, 1), Now);
        Assert.Throws<ArgumentException>(() => queue.TryEnqueue(new(Guid.NewGuid(), window, Now)));
        Assert.Throws<ArgumentException>(() => queue.TryEnqueue(
            new(Guid.NewGuid(), SchedulerSettingsTests.Entry(), Now, trigger)));
        Assert.Throws<ArgumentException>(() => queue.TryEnqueue(
            new(Guid.NewGuid(), window, Now.AddSeconds(1), trigger)));
        Assert.Throws<ArgumentException>(() => queue.TryEnqueue(
            new(Guid.NewGuid(), window, Now, new(default, Now))));
    }

    [Fact]
    public void NonOwnerAndStoppedMonitorCannotBeginObservation()
    {
        using var host = new Host(Entry());
        var gate = new SchedulerExecutionGate();
        gate.SetInputCleanupConfirmed(true);
        using var contender = new SchedulerMonitorSession(
            new SchedulerSettingsController(host.Path), new SchedulerApplicationSession(),
            gate, host.LeasePath);
        Assert.False(contender.TryStart(Now, TimeZoneInfo.Utc));
        Assert.Null(contender.BeginWindowObservation());
        host.Session.StopAccepting();
        Assert.Null(host.Session.BeginWindowObservation());
    }

    private static SchedulerEntry Entry() =>
        SchedulerSettingsTests.Entry(new SchedulerWindowCondition("対象", "Main"));

    [Fact]
    public void OwnershipLossRejectsDelayedObservationAndDoesNotSave()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "uwsc-window-owner-" + Guid.NewGuid().ToString("N"));
        string path = System.IO.Path.Combine(directory, "schedule.json");
        try
        {
            SchedulerSettingsStore.Save(path, new SchedulerSettings([Entry()], enabled: true), null);
            var controller = new SchedulerSettingsController(path);
            bool owner = true;
            var runtime = new SchedulerRuntime(controller, () => owner);
            Assert.True(runtime.Start(Now, TimeZoneInfo.Utc));
            Guid epoch = Assert.IsType<Guid>(runtime.BeginWindowObservation());
            Assert.Equal(1, runtime.ObserveWindows(epoch, Snapshot(1)));
            byte[] before = File.ReadAllBytes(path);
            owner = false;
            Assert.Equal(0, runtime.ObserveWindows(epoch, Snapshot(2)));
            Assert.False(runtime.IsActive);
            Assert.Equal(0, runtime.PendingCount);
            Assert.NotNull(runtime.LastError);
            Assert.Equal(before, File.ReadAllBytes(path));
            owner = true;
            Assert.False(runtime.Start(Now, TimeZoneInfo.Utc));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClockOnlyMonitorDoesNotRequestWindowObservation()
    {
        using var host = new Host(SchedulerSettingsTests.Entry());
        Assert.Null(host.Session.BeginWindowObservation());
    }

    private static SchedulerWindowSnapshot Snapshot(params ulong[] handles) =>
        SchedulerWindowSnapshot.Complete(Now, handles.Select(handle =>
            new SchedulerObservedWindow(new(handle, 1, 1), "対象", "Main")));

    private sealed class Host : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "uwsc-window-queue-" + Guid.NewGuid().ToString("N"));
        private readonly List<SchedulerDispatch> active = [];

        internal Host(params SchedulerEntry[] entries)
        {
            Save(entries);
            var gate = new SchedulerExecutionGate();
            gate.SetInputCleanupConfirmed(true);
            Session = new(new SchedulerSettingsController(Path),
                new SchedulerApplicationSession(), gate, LeasePath);
            Assert.True(Session.TryStart(Now, TimeZoneInfo.Utc));
        }

        internal string Path => System.IO.Path.Combine(directory, "schedule.json");

        internal string LeasePath => System.IO.Path.Combine(directory, "schedule.monitor");

        internal SchedulerMonitorSession Session
        {
            get;
        }

        internal Guid Begin() => Assert.IsType<Guid>(Session.BeginWindowObservation());

        internal int Observe(Guid epoch, params ulong[] handles) =>
            Session.ObserveWindows(epoch, Snapshot(handles));

        internal void Save(params SchedulerEntry[] entries) => SaveEnabled(true, entries);

        internal void SaveEnabled(bool enabled, params SchedulerEntry[] entries)
        {
            SchedulerSettingsSnapshot old = SchedulerSettingsStore.Load(Path);
            SchedulerSettingsStore.Save(Path, new SchedulerSettings(entries, enabled: enabled), old.Revision);
        }

        internal SchedulerDispatch Take()
        {
            return Assert.IsType<SchedulerDispatch>(TryTake());
        }

        internal SchedulerDispatch? TryTake()
        {
            SchedulerDispatch? dispatch = Session.TryDispatch();
            if (dispatch is not null)
            {
                active.Add(dispatch);
            }
            return dispatch;
        }

        internal void Complete(SchedulerDispatch dispatch)
        {
            Assert.True(Session.Complete(dispatch, succeeded: true, resourcesRecovered: true));
            active.Remove(dispatch);
        }

        public void Dispose()
        {
            foreach (SchedulerDispatch dispatch in active.ToArray())
            {
                Complete(dispatch);
            }
            Session.StopAccepting();
            Session.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
