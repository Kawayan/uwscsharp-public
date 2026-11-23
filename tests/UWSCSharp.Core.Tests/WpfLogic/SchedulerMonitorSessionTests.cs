using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerMonitorSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-schedule-monitor-" + Guid.NewGuid().ToString("N"));
    private readonly List<SchedulerMonitorSession> sessions = [];
    private readonly List<(SchedulerMonitorSession Session, SchedulerDispatch Dispatch)> jobs = [];
    private static readonly DateTimeOffset Now =
        new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

    private string SettingsPath => Path.Combine(directory, "schedule.json");

    private string LeasePath => Path.Combine(directory, "schedule.monitor");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispatchCapturesCurrentSoundOptionAndDoesNotChangeAfterSave(bool beep)
    {
        Initialize([SchedulerSettingsTests.Entry()]);
        var editor = new SchedulerSettingsController(SettingsPath);
        editor.Reload();
        editor.Save(editor.Current!.Settings.WithOptions(true, beep, false));
        SchedulerMonitorSession session = Create();
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        SchedulerDispatch dispatch = Assert.IsType<SchedulerDispatch>(session.TryDispatch());
        jobs.Add((session, dispatch));
        Assert.Equal(beep, dispatch.Beep);
        editor.Reload();
        editor.Save(editor.Current!.Settings.WithOptions(true, !beep, false));
        Assert.Equal(beep, dispatch.Beep);
    }

    [Fact]
    public void BusyOwnershipDoesNotDisableSettingsOrAutomaticallyTakeOver()
    {
        Initialize([SchedulerSettingsTests.Entry()]);
        using SchedulerMonitorSession first = Create();
        using SchedulerMonitorSession second = Create();
        Assert.True(first.TryStart(Now, TimeZoneInfo.Utc));
        Guid? revision = SchedulerSettingsStore.Load(SettingsPath).Revision;
        Assert.False(second.TryStart(Now, TimeZoneInfo.Utc));
        Assert.True(SchedulerSettingsStore.Load(SettingsPath).Settings.Enabled);
        Assert.Equal(revision, SchedulerSettingsStore.Load(SettingsPath).Revision);
        Assert.Null(second.TryDispatch());
        first.StopAccepting();
        second.Poll(Now.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.False(second.OwnsMonitor);
        Assert.True(second.TryStart(Now.AddMinutes(1), TimeZoneInfo.Utc));
        Assert.True(second.OwnsMonitor);
    }

    [Fact]
    public void OwnershipAndSaveLocksAreIndependentAndLeaseFileIsRetained()
    {
        Initialize([]);
        using SchedulerMonitorSession session = Create();
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        var editor = new SchedulerSettingsController(SettingsPath);
        editor.Reload();
        editor.Save(editor.Current!.Settings.WithOptions(true, true, false));
        Assert.True(SchedulerSettingsStore.Load(SettingsPath).Settings.Beep);
        Assert.Throws<IOException>(() => File.Delete(LeasePath));
        session.StopAccepting();
        Assert.True(File.Exists(LeasePath));
        using SchedulerMonitorLease lease = Assert.IsType<SchedulerMonitorLease>(
            SchedulerMonitorLease.TryAcquire(LeasePath));
        Assert.True(lease.IsActive);
    }

    [Fact]
    public void InvalidOwnershipTargetIsDiagnosedInsteadOfReportedBusy()
    {
        Initialize([]);
        Directory.CreateDirectory(LeasePath);
        SchedulerMonitorSession session = Create();
        Assert.False(session.TryStart(Now, TimeZoneInfo.Utc));
        Assert.NotNull(session.LastError);
        Assert.False(session.OwnsMonitor);
    }

    [Fact]
    public void TimeZoneChangeStopsAndDoesNotAutomaticallyReacquire()
    {
        Initialize([SchedulerSettingsTests.Entry()]);
        SchedulerMonitorSession session = Create();
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        var changed = TimeZoneInfo.CreateCustomTimeZone(
            "MonitorChanged", TimeSpan.FromHours(1), "MonitorChanged", "MonitorChanged");
        session.Poll(Now.AddMinutes(1), changed);
        Assert.False(session.IsActive);
        Assert.False(session.OwnsMonitor);
        Assert.IsType<InvalidOperationException>(session.LastError);
        session.Poll(Now.AddMinutes(2), changed);
        Assert.False(session.OwnsMonitor);
    }

    [Fact]
    public void FourDispatchesHoldSlotsUntilCleanupAndIgnoreRepeatedCompletion()
    {
        Initialize(Enumerable.Range(0, 6)
            .Select(_ => SchedulerSettingsTests.Entry()));
        SchedulerExecutionGate gate = Ready();
        SchedulerMonitorSession session = Create(gate);
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        SchedulerDispatch[] first = [.. Enumerable.Range(0, 4).Select(_ => Take(session))];
        Assert.Equal(4, gate.ScheduledCount);
        Assert.Null(session.TryDispatch());
        Assert.False(session.Complete(first[0], true, resourcesRecovered: false));
        Assert.Equal(4, gate.ScheduledCount);
        Assert.True(session.Complete(first[0], true, resourcesRecovered: true));
        _ = Take(session);
        Assert.False(session.Complete(first[0], true, resourcesRecovered: true));
        Assert.Equal(4, gate.ScheduledCount);
    }

    [Fact]
    public void EditorActivityLetsSeparateRequestPassWaitingSameProcess()
    {
        SchedulerEntry separate = SchedulerSettingsTests.Entry();
        var same = new SchedulerEntry(Guid.NewGuid(), "同一", separate.Path, [],
            SchedulerExecutionMode.SameProcess, new SchedulerStartupCondition(), directory);
        Initialize([same, separate]);
        SchedulerExecutionGate gate = Ready();
        SchedulerMonitorSession session = Create(gate);
        using IDisposable editor = Assert.IsAssignableFrom<IDisposable>(
            gate.TryEnterInteractive());
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        Assert.Equal(separate.Id, Take(session).Request.Entry.Id);
        Assert.Null(session.TryDispatch());
        editor.Dispose();
        Assert.Equal(same.Id, Take(session).Request.Entry.Id);
        Assert.Null(gate.TryEnterInteractive());
    }

    [Fact]
    public void StopAndDisposeCannotReleaseUnrecoveredExecutionOwnership()
    {
        Initialize([SchedulerSettingsTests.Entry()]);
        SchedulerExecutionGate gate = Ready();
        SchedulerMonitorSession session = Create(gate);
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        SchedulerDispatch dispatch = Take(session);
        session.StopAccepting();
        Assert.False(session.IsActive);
        Assert.True(session.OwnsMonitor);
        Assert.Throws<InvalidOperationException>(session.Dispose);
        using SchedulerMonitorSession other = Create();
        Assert.False(other.TryStart(Now, TimeZoneInfo.Utc));
        Assert.False(session.Complete(dispatch, true, resourcesRecovered: false));
        Assert.Equal(1, gate.ScheduledCount);
        Assert.True(session.Complete(dispatch, true, resourcesRecovered: true));
        Assert.False(session.OwnsMonitor);
        Assert.Equal(0, gate.ScheduledCount);
        Assert.True(other.TryStart(Now, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptReloadStopsAcceptanceButKeepsOwnedExecution(bool running)
    {
        Initialize([SchedulerSettingsTests.Entry()]);
        SchedulerMonitorSession session = Create();
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        if (running)
        {
            _ = Take(session);
        }

        File.WriteAllText(SettingsPath, "{");
        session.Poll(Now.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.False(session.IsActive);
        Assert.Equal(running, session.OwnsMonitor);
        Assert.IsType<InvalidDataException>(session.LastError);
        Assert.Null(session.TryDispatch());
        Assert.Equal("{", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void ExternalDisableKeepsExecutionAndReleasesAfterCleanup()
    {
        Initialize([SchedulerSettingsTests.Entry(), SchedulerSettingsTests.Entry()]);
        SchedulerMonitorSession session = Create();
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        SchedulerDispatch dispatch = Take(session);
        var editor = new SchedulerSettingsController(SettingsPath);
        editor.Reload();
        editor.Save(editor.Current!.Settings.WithOptions(false, false, false));
        session.Poll(Now.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.False(session.IsActive);
        Assert.True(session.OwnsMonitor);
        Assert.Null(session.TryDispatch());
        Assert.True(session.Complete(dispatch, true, resourcesRecovered: true));
        Assert.False(session.OwnsMonitor);
    }

    [Fact]
    public void SameApplicationDoesNotReplayStartupAfterExplicitReacquisition()
    {
        Initialize([SchedulerSettingsTests.Entry()]);
        var application = new SchedulerApplicationSession();
        SchedulerMonitorSession session = Create(application: application);
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        SchedulerDispatch first = Take(session);
        Assert.True(session.Complete(first, true, resourcesRecovered: true));
        session.StopAccepting();
        Assert.True(session.TryStart(Now.AddMinutes(1), TimeZoneInfo.Utc));
        Assert.Null(session.TryDispatch());
        Assert.False(session.Complete(first, true, resourcesRecovered: true));
        session.StopAccepting();
        SchedulerMonitorSession newApplication = Create();
        Assert.True(newApplication.TryStart(Now, TimeZoneInfo.Utc));
        Assert.NotEqual(first.Generation, Take(newApplication).Generation);
    }

    [Fact]
    public void OnlyOwnerRecoversPreviousOneShotAndNeverRetriesIt()
    {
        DateTime target = Now.DateTime.AddMinutes(1);
        SchedulerEntry entry = SchedulerSettingsTests.Entry(new SchedulerOneShotCondition(
            DateOnly.FromDateTime(target), TimeOnly.FromDateTime(target)));
        var condition = (SchedulerOneShotCondition)entry.Condition;
        var state = new SchedulerOneShotState(entry.Id, condition.Date,
            condition.Time, Guid.NewGuid(), Now, SchedulerOneShotStatus.Started);
        SchedulerSettingsStore.Save(SettingsPath,
            new SchedulerSettings([entry], enabled: true, oneShotStates: [state]), null);
        SchedulerMonitorSession session = Create();
        using (SchedulerMonitorLease held = Assert.IsType<SchedulerMonitorLease>(
            SchedulerMonitorLease.TryAcquire(LeasePath)))
        {
            Assert.False(session.TryStart(Now, TimeZoneInfo.Utc));
            Assert.Equal(SchedulerOneShotStatus.Started, Assert.Single(
                SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates).Status);
        }

        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        Assert.Equal(SchedulerOneShotStatus.Unknown, Assert.Single(
            SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates).Status);
        session.Poll(Now.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.Null(session.TryDispatch());
    }

    [Fact]
    public void UnknownCleanupDoesNotHandOffOrMarkOneShotStarted()
    {
        DateTime target = Now.DateTime.AddMinutes(1);
        Initialize([SchedulerSettingsTests.Entry(new SchedulerOneShotCondition(
            DateOnly.FromDateTime(target), TimeOnly.FromDateTime(target)))]);
        var gate = new SchedulerExecutionGate();
        SchedulerMonitorSession session = Create(gate);
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        session.Poll(Now.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.Null(session.TryDispatch());
        Assert.Equal(SchedulerOneShotStatus.Accepted, Assert.Single(
            SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates).Status);
        gate.SetInputCleanupConfirmed(true);
        SchedulerDispatch dispatch = Take(session);
        Assert.Equal(SchedulerOneShotStatus.Started, Assert.Single(
            SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates).Status);
        Assert.False(session.Complete(dispatch with
        {
            Generation = Guid.NewGuid()
        },
            true, resourcesRecovered: true));
        Assert.Equal(1, gate.ScheduledCount);
    }

    [Fact]
    public async Task TimerMustEndBeforeSessionCanBeDisposed()
    {
        Initialize([]);
        SchedulerMonitorSession session = Create();
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        using var cancellation = new CancellationTokenSource();
        Task timer = session.RunTimerAsync(TimeProvider.System,
            () => TimeZoneInfo.Utc, cancellation.Token);
        try
        {
            Assert.Throws<InvalidOperationException>(session.Dispose);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.RunTimerAsync(TimeProvider.System,
                    () => TimeZoneInfo.Utc, cancellation.Token));
        }
        finally
        {
            cancellation.Cancel();
            await timer.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(session.OwnsMonitor);
        session.Dispose();
    }

    [Fact]
    public async Task DefaultTimerReadsExternalDisableAndStopsOwner()
    {
        Initialize([]);
        SchedulerMonitorSession session = Create();
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        var editor = new SchedulerSettingsController(SettingsPath);
        editor.Reload();
        editor.Save(editor.Current!.Settings.WithOptions(false, false, false));
        using var cancellation = new CancellationTokenSource();
        Task timer = session.RunTimerAsync(TimeProvider.System,
            () => TimeZoneInfo.Utc, cancellation.Token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (session.OwnsMonitor)
            {
                await Task.Delay(10, deadline.Token);
            }

            Assert.False(session.IsActive);
        }
        finally
        {
            cancellation.Cancel();
            await timer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DisposedSessionCannotStartAnotherTimer()
    {
        Initialize([]);
        SchedulerMonitorSession session = Create();
        session.Dispose();
        Task timer = session.RunTimerAsync(TimeProvider.System,
            () => TimeZoneInfo.Utc, CancellationToken.None);
        Assert.True(timer.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => timer);
    }

    [Fact]
    public async Task ClockFailureIsReportedAndReleasesIdleOwner()
    {
        Initialize([]);
        SchedulerMonitorSession session = Create();
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        var failure = new InvalidOperationException("検査用の時計失敗");
        Task timer = session.RunTimerAsync(TimeProvider.System,
            () => throw failure, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            timer.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(failure, session.LastError);
        Assert.False(session.IsActive);
        Assert.False(session.OwnsMonitor);
    }

    [Theory]
    [InlineData("success", true, "Succeeded")]
    [InlineData("failure", true, "Failed")]
    [InlineData("cancel", false, "Cancelled")]
    [InlineData("cancel", true, "Failed")]
    [InlineData("unknown", true, "Unknown")]
    [InlineData("start-failure", false, "Failed")]
    public void TypedCompletionPersistsConsumption(string kind,
        bool started, string expected)
    {
        SchedulerDispatch dispatch = TakeOneShot(out SchedulerMonitorSession session,
            out SchedulerExecutionGate gate);
        SchedulerExecutionResult result = kind switch
        {
            "success" => SchedulerExecutionResult.Completed(0, started, false, true),
            "failure" => SchedulerExecutionResult.Completed(1, started, false, true),
            "cancel" => SchedulerExecutionResult.Completed(130, started, true, true),
            "start-failure" => SchedulerExecutionResult.FailedToStart("start rejected"),
            _ => SchedulerExecutionResult.Unknown(started, 0, true)
        };
        Assert.True(session.Complete(dispatch, result));
        Assert.Equal(expected, SavedState().Status.ToString());
        Assert.Equal(0, gate.ScheduledCount);
        byte[] saved = File.ReadAllBytes(SettingsPath);
        Assert.False(session.Complete(dispatch, result));
        Assert.False(session.ConfirmRecovery(dispatch));
        Assert.Equal(saved, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void ResultIsSavedBeforeRecoveryAndRetainsOwnershipAndRow()
    {
        SchedulerDispatch dispatch = TakeOneShot(out SchedulerMonitorSession session,
            out SchedulerExecutionGate gate);
        Assert.False(session.ConfirmRecovery(dispatch));
        var result = SchedulerExecutionResult.Unknown(
            true, 0, false, "input not confirmed", forced: true);
        Assert.True(session.Complete(dispatch, result));
        Assert.Equal(SchedulerOneShotStatus.Unknown, SavedState().Status);
        Assert.Equal(1, gate.ScheduledCount);
        gate.SetInputCleanupConfirmed(true);
        Assert.False(gate.CanEnterInteractive);
        Assert.Null(gate.TryEnterInteractive());
        Assert.Null(gate.TryBeginDispatch());
        Assert.False(session.Complete(dispatch,
            SchedulerExecutionResult.Completed(0, true, false, true)));
        session.StopAccepting();
        Assert.True(session.OwnsMonitor);
        Assert.Throws<InvalidOperationException>(session.Dispose);
        using SchedulerMonitorSession other = Create();
        Assert.False(other.TryStart(Now.AddMinutes(2), TimeZoneInfo.Utc));
        Assert.False(session.ConfirmRecovery(dispatch with
        {
            Generation = Guid.NewGuid()
        }));
        Assert.False(session.ConfirmRecovery(dispatch with
        {
            Request = dispatch.Request with
            {
                ObservedAt = Now.AddHours(1)
            }
        }));
        Assert.Equal(1, gate.ScheduledCount);
        Assert.True(session.ConfirmRecovery(dispatch));
        Assert.False(session.OwnsMonitor);
        Assert.Equal(0, gate.ScheduledCount);
        Assert.Equal(SchedulerOneShotStatus.Unknown, SavedState().Status);
        Assert.True(other.TryStart(Now.AddMinutes(2), TimeZoneInfo.Utc));
        Assert.Null(other.TryDispatch());
    }

    [Fact]
    public void ConflictingResultSaveStopsAdmissionButDoesNotInventSavedSuccess()
    {
        SchedulerDispatch dispatch = TakeOneShot(out SchedulerMonitorSession session,
            out SchedulerExecutionGate gate);
        var editor = new SchedulerSettingsController(SettingsPath);
        editor.Reload();
        editor.Save(editor.Current!.Settings.WithOptions(true, true, false));
        Assert.False(session.Complete(dispatch,
            SchedulerExecutionResult.Completed(0, true, false, false)));
        Assert.NotNull(session.LastError);
        Assert.False(session.IsActive);
        Assert.Equal(SchedulerOneShotStatus.Started, SavedState().Status);
        Assert.Equal(1, gate.ScheduledCount);
        Assert.True(session.ConfirmRecovery(dispatch));
        Assert.Equal(0, gate.ScheduledCount);
        Assert.False(session.OwnsMonitor);
    }

    [Fact]
    public void LateOldResultCannotChangeRearmedDefinitionOrNewOccurrence()
    {
        SchedulerDispatch old = TakeOneShot(out SchedulerMonitorSession session,
            out SchedulerExecutionGate gate);
        var editor = new SchedulerSettingsController(SettingsPath);
        editor.Reload();
        SchedulerEntry previous = old.Request.Entry;
        var future = new SchedulerEntry(previous.Id, "future", previous.Path, [],
            previous.Mode, new SchedulerOneShotCondition(
                DateOnly.FromDateTime(Now.DateTime),
                TimeOnly.FromDateTime(Now.AddMinutes(3).DateTime)), directory);
        editor.SaveEntry(0, future, Now.AddMinutes(1), TimeZoneInfo.Utc);
        session.Poll(Now.AddMinutes(2), TimeZoneInfo.Utc);
        byte[] changed = File.ReadAllBytes(SettingsPath);
        Assert.True(session.Complete(old,
            SchedulerExecutionResult.Unknown(true, null, true)));
        Assert.Equal(changed, File.ReadAllBytes(SettingsPath));
        session.Poll(Now.AddMinutes(3), TimeZoneInfo.Utc);
        SchedulerDispatch next = Take(session);
        Assert.NotEqual(old.Request.OccurrenceId, next.Request.OccurrenceId);
        changed = File.ReadAllBytes(SettingsPath);
        Assert.False(session.Complete(old,
            SchedulerExecutionResult.Completed(0, true, false, true)));
        Assert.False(session.ConfirmRecovery(old));
        Assert.Equal(changed, File.ReadAllBytes(SettingsPath));
        Assert.Equal(1, gate.ScheduledCount);
        Assert.True(session.Complete(next,
            SchedulerExecutionResult.Completed(0, true, false, true)));
        Assert.Equal(SchedulerOneShotStatus.Succeeded, SavedState().Status);
    }

    private SchedulerDispatch TakeOneShot(out SchedulerMonitorSession session,
        out SchedulerExecutionGate gate)
    {
        Initialize([SchedulerSettingsTests.Entry(new SchedulerOneShotCondition(
            DateOnly.FromDateTime(Now.DateTime),
            TimeOnly.FromDateTime(Now.AddMinutes(1).DateTime)))]);
        gate = Ready();
        session = Create(gate);
        Assert.True(session.TryStart(Now, TimeZoneInfo.Utc));
        session.Poll(Now.AddMinutes(1), TimeZoneInfo.Utc);
        return Take(session);
    }

    private SchedulerOneShotState SavedState() => Assert.Single(
        SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates);

    private void Initialize(IEnumerable<SchedulerEntry> entries) =>
        SchedulerSettingsStore.Save(SettingsPath,
            new SchedulerSettings(entries, enabled: true), null);

    private SchedulerMonitorSession Create(SchedulerExecutionGate? gate = null,
        SchedulerApplicationSession? application = null)
    {
        var session = new SchedulerMonitorSession(
            new SchedulerSettingsController(SettingsPath),
            application ?? new SchedulerApplicationSession(),
            gate ?? Ready(), LeasePath);
        sessions.Add(session);
        return session;
    }

    private SchedulerDispatch Take(SchedulerMonitorSession session)
    {
        SchedulerDispatch dispatch = Assert.IsType<SchedulerDispatch>(session.TryDispatch());
        jobs.Add((session, dispatch));
        return dispatch;
    }

    private static SchedulerExecutionGate Ready()
    {
        var gate = new SchedulerExecutionGate();
        gate.SetInputCleanupConfirmed(true);
        return gate;
    }

    public void Dispose()
    {
        foreach ((SchedulerMonitorSession session, SchedulerDispatch dispatch) in jobs)
        {
            try
            {
                session.ConfirmRecovery(dispatch);
                session.Complete(dispatch, succeeded: false, resourcesRecovered: true);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        foreach (SchedulerMonitorSession session in sessions)
        {
            session.Dispose();
        }

        string target = Path.GetFullPath(directory);
        string parent = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(target) != parent
            || !Path.GetFileName(target).StartsWith(
                "uwsc-schedule-monitor-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("監視テストの回収先が範囲外です。");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
