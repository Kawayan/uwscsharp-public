using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "uwsc-schedule-runtime-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Beginning = new(2027, 1, 2, 3, 0, 0, TimeSpan.Zero);

    private string SettingsPath => Path.Combine(directory, "schedule.json");

    [Fact]
    public void PersistsConsumptionBeforePublishingAndStartedBeforeHandoff()
    {
        SchedulerEntry entry = OneShot(1);
        (SchedulerRuntime runtime, _) = Create([entry]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.Equal(1, runtime.PendingCount);
        Assert.Equal(SchedulerOneShotStatus.Accepted, SavedState().Status);
        SchedulerExecutionRequest request = Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake());
        Assert.Equal(SchedulerOneShotStatus.Started, SavedState().Status);
        Assert.Equal(request.OccurrenceId, SavedState().OccurrenceId);
        Assert.True(runtime.Complete(request.OccurrenceId, true));
        Assert.Equal(SchedulerOneShotStatus.Succeeded, SavedState().Status);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.Equal(0, runtime.PendingCount);
        Assert.Null(runtime.TryTake());
    }

    [Fact]
    public void ConsumptionSaveFailureStopsWithoutPublishing()
    {
        (SchedulerRuntime runtime, _) = Create([OneShot(1)]);
        byte[] original = File.ReadAllBytes(SettingsPath);
        using (var ownership = new FileStream(SettingsPath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
            Assert.False(runtime.IsActive);
            Assert.IsAssignableFrom<IOException>(runtime.LastError);
            Assert.Equal(0, runtime.PendingCount);
            Assert.Null(runtime.TryTake());
        }

        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Empty(SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates);
    }

    [Fact]
    public void RechecksChangedDefinitionImmediatelyBeforeHandoff()
    {
        SchedulerEntry entry = SchedulerSettingsTests.Entry(new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1));
        (SchedulerRuntime runtime, SchedulerSettingsController controller) = Create([entry]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        var changed = new SchedulerEntry(entry.Id, entry.Title, entry.Path, ["new"],
            entry.Mode, entry.Condition, directory);
        controller.Save(controller.Current!.Settings.WithSlot(0, changed));
        Assert.Null(runtime.TryTake());
        Assert.Equal(0, runtime.PendingCount);
    }

    [Fact]
    public void CancellationRemainsSavedWhenSameTickAcceptsAnotherOneShot()
    {
        SchedulerEntry first = OneShot(1);
        SchedulerEntry second = OneShot(2);
        (SchedulerRuntime runtime, SchedulerSettingsController controller) = Create([first, second]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        var changed = new SchedulerEntry(first.Id, first.Title, first.Path, ["new"],
            first.Mode, first.Condition, directory);
        controller.Save(controller.Current!.Settings.WithSlot(0, changed));
        runtime.Tick(Beginning.AddMinutes(2), TimeZoneInfo.Utc);
        IReadOnlyList<SchedulerOneShotState> states = SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates;
        Assert.Equal(SchedulerOneShotStatus.Cancelled, states.Single(state => state.EntryId == first.Id).Status);
        Assert.Equal(SchedulerOneShotStatus.Accepted, states.Single(state => state.EntryId == second.Id).Status);
        Assert.Equal(1, runtime.PendingCount);
    }

    [Fact]
    public void DisableBeforeNextTickCancelsInsteadOfHandingOff()
    {
        (SchedulerRuntime runtime, SchedulerSettingsController controller) = Create([OneShot(1)]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        controller.Save(controller.Current!.Settings.WithOptions(false, false, false));
        Assert.Null(runtime.TryTake());
        Assert.False(runtime.IsActive);
        Assert.Equal(SchedulerOneShotStatus.Cancelled, SavedState().Status);
    }

    [Fact]
    public void FailedStartedSaveDoesNotHandOffOrAutomaticallyRetry()
    {
        (SchedulerRuntime runtime, _) = Create([OneShot(1)]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        using (var ownership = new FileStream(SettingsPath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(runtime.TryTake());
            Assert.False(runtime.IsActive);
            Assert.Equal(0, runtime.RunningCount);
            Assert.Equal(SchedulerOneShotStatus.Accepted, SavedState().Status);
        }

        Assert.True(runtime.Reload(Beginning.AddMinutes(2), TimeZoneInfo.Utc));
        Assert.Equal(SchedulerOneShotStatus.Unknown, SavedState().Status);
        Assert.Null(runtime.TryTake());
    }

    [Fact]
    public void StaleCompletionCannotOverwriteRearmedOccurrence()
    {
        SchedulerEntry first = OneShot(1);
        (SchedulerRuntime runtime, SchedulerSettingsController controller) = Create([first]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        SchedulerExecutionRequest old = Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake());
        var changed = new SchedulerEntry(first.Id, first.Title, first.Path, first.Arguments,
            first.Mode, new SchedulerOneShotCondition(DateOnly.FromDateTime(Beginning.DateTime),
                TimeOnly.FromDateTime(Beginning.DateTime.AddMinutes(2))), directory);
        controller.Save(controller.Current!.Settings.WithSlot(0, changed));
        runtime.Tick(Beginning.AddMinutes(2), TimeZoneInfo.Utc);
        SchedulerOneShotState newer = SavedState();
        Assert.NotEqual(old.OccurrenceId, newer.OccurrenceId);
        Assert.True(runtime.Complete(old.OccurrenceId, false));
        Assert.Equal(newer.OccurrenceId, SavedState().OccurrenceId);
        Assert.Equal(SchedulerOneShotStatus.Accepted, SavedState().Status);
        SchedulerExecutionRequest next = Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake());
        Assert.Equal(newer.OccurrenceId, next.OccurrenceId);
    }

    [Fact]
    public void NewOwnerRecoversUnconfirmedConsumptionWithoutRetry()
    {
        SchedulerEntry entry = OneShot(1);
        SchedulerSettingsSnapshot initial = SchedulerSettingsStore.Save(SettingsPath,
            new SchedulerSettings([entry], enabled: true), null);
        var controller = new SchedulerSettingsController(SettingsPath);
        bool owned = true;
        var previous = new SchedulerRuntime(controller, () => owned);
        Assert.True(previous.Start(Beginning, TimeZoneInfo.Utc));
        previous.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        Guid occurrence = SavedState().OccurrenceId;
        owned = false;
        var current = new SchedulerRuntime(new SchedulerSettingsController(SettingsPath), () => true);
        Assert.True(current.Start(Beginning.AddMinutes(5), TimeZoneInfo.Utc));
        Assert.Equal(SchedulerOneShotStatus.Unknown, SavedState().Status);
        Assert.Equal(occurrence, SavedState().OccurrenceId);
        Assert.Null(current.TryTake());
        Assert.Null(previous.TryTake());
        Assert.False(previous.IsActive);
        Assert.NotEqual(initial.Revision, SchedulerSettingsStore.Load(SettingsPath).Revision);
    }

    [Fact]
    public void StartupIsNotReplayedAndClockJumpDoesNotCatchUpMissedOneShot()
    {
        SchedulerEntry startup = SchedulerSettingsTests.Entry();
        SchedulerEntry interval = SchedulerSettingsTests.Entry(new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1));
        (SchedulerRuntime runtime, _) = Create([startup, OneShot(1), interval]);
        SchedulerExecutionRequest first = Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake());
        Assert.Equal(startup.Id, first.Entry.Id);
        runtime.Complete(first.OccurrenceId, true);
        runtime.Stop();
        Assert.True(runtime.Start(Beginning, TimeZoneInfo.Utc));
        Assert.Equal(0, runtime.PendingCount);
        runtime.Tick(Beginning.AddMinutes(7), TimeZoneInfo.Utc);
        Assert.Equal(interval.Id, Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake()).Entry.Id);
        Assert.Empty(SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates);
        runtime.Tick(Beginning.AddMinutes(2), TimeZoneInfo.Utc);
        Assert.Equal(SchedulerMinuteStatus.ClockBehind, runtime.LastObservation!.Status);
        Assert.Equal(0, runtime.PendingCount);
    }

    [Fact]
    public void ZoneChangeCancelsPendingAndRetainsConsumption()
    {
        (SchedulerRuntime runtime, _) = Create([OneShot(1)]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        var changed = TimeZoneInfo.CreateCustomTimeZone("changed", TimeSpan.Zero, "検査", "検査");
        runtime.Tick(Beginning.AddMinutes(1), changed);
        Assert.False(runtime.IsActive);
        Assert.Equal(0, runtime.PendingCount);
        Assert.Equal(SchedulerOneShotStatus.Cancelled, SavedState().Status);
        Assert.True(runtime.Start(Beginning.AddMinutes(1), changed));
        Assert.Empty(runtime.RowErrors);
        Assert.Null(runtime.TryTake());
    }

    [Fact]
    public void StaleFileRevisionStopsAcceptanceWithoutOverwritingOtherSave()
    {
        (SchedulerRuntime runtime, SchedulerSettingsController controller) = Create([OneShot(1)]);
        SchedulerSettingsStore.Save(SettingsPath, controller.Current!.Settings, controller.Current.Revision);
        byte[] other = File.ReadAllBytes(SettingsPath);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.IsType<SchedulerSettingsConflictException>(runtime.LastError);
        Assert.False(runtime.IsActive);
        Assert.Equal(0, runtime.PendingCount);
        Assert.Equal(other, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void DomainErrorIsPerRowAndCanRecoverAtNextMinute()
    {
        SchedulerEntry expression = SchedulerSettingsTests.Entry(new SchedulerExpressionCondition("1/(NN-1)"));
        SchedulerEntry interval = SchedulerSettingsTests.Entry(new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1));
        (SchedulerRuntime runtime, _) = Create([expression, interval]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.True(runtime.IsActive);
        Assert.Contains(expression.Id, runtime.RowErrors.Keys);
        SchedulerExecutionRequest first = Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake());
        Assert.Equal(interval.Id, first.Entry.Id);
        runtime.Complete(first.OccurrenceId, true);
        runtime.Tick(Beginning.AddMinutes(2), TimeZoneInfo.Utc);
        Assert.Empty(runtime.RowErrors);
        Assert.Equal(2, runtime.PendingCount);
        Assert.Equal(expression.Id, Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake()).Entry.Id);
    }

    [Fact]
    public void CorruptReloadCancelsPendingAndKeepsRawFile()
    {
        (SchedulerRuntime runtime, _) = Create([SchedulerSettingsTests.Entry()]);
        Assert.Equal(1, runtime.PendingCount);
        File.WriteAllText(SettingsPath, "{");
        Assert.False(runtime.Reload(Beginning, TimeZoneInfo.Utc));
        Assert.False(runtime.IsActive);
        Assert.Equal(0, runtime.PendingCount);
        Assert.NotNull(runtime.LastError);
        Assert.Equal("{", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void NewStartupRowWaitsUntilNextRuntimeSession()
    {
        SchedulerEntry first = SchedulerSettingsTests.Entry();
        (SchedulerRuntime runtime, SchedulerSettingsController controller) = Create([first]);
        SchedulerExecutionRequest request = Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake());
        runtime.Complete(request.OccurrenceId, true);
        controller.Save(controller.Current!.Settings.WithSlot(1, SchedulerSettingsTests.Entry()));
        Assert.True(runtime.Start(Beginning, TimeZoneInfo.Utc));
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.Equal(0, runtime.PendingCount);
        runtime.Stop();
        var restarted = new SchedulerRuntime(new SchedulerSettingsController(SettingsPath), () => true);
        Assert.True(restarted.Start(Beginning.AddMinutes(2), TimeZoneInfo.Utc));
        Assert.Equal(2, restarted.PendingCount);
    }

    [Fact]
    public void CompletionSaveFailureLeavesUnknownAfterExplicitRecovery()
    {
        (SchedulerRuntime runtime, _) = Create([OneShot(1)]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        SchedulerExecutionRequest request = Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake());
        using (var ownership = new FileStream(SettingsPath + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(runtime.Complete(request.OccurrenceId, true));
            Assert.Equal(0, runtime.RunningCount);
            Assert.Equal(SchedulerOneShotStatus.Started, SavedState().Status);
        }

        Assert.True(runtime.Reload(Beginning.AddMinutes(2), TimeZoneInfo.Utc));
        Assert.Equal(SchedulerOneShotStatus.Unknown, SavedState().Status);
        Assert.Null(runtime.TryTake());
    }

    [Fact]
    public void OwnershipLossAfterConsumptionSavePreventsPublication()
    {
        SchedulerSettingsStore.Save(SettingsPath, new SchedulerSettings([OneShot(1)], enabled: true), null);
        var controller = new SchedulerSettingsController(SettingsPath);
        bool checkAfterSave = false;
        var runtime = new SchedulerRuntime(controller, () => !checkAfterSave
            || SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates.Count == 0);
        Assert.True(runtime.Start(Beginning, TimeZoneInfo.Utc));
        checkAfterSave = true;
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        Assert.False(runtime.IsActive);
        Assert.Equal(0, runtime.PendingCount);
        Assert.Equal(SchedulerOneShotStatus.Accepted, SavedState().Status);
        Assert.Null(runtime.TryTake());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedOrReplacedConsumptionCannotAuthorizeOldHandoff(bool replace)
    {
        (SchedulerRuntime runtime, SchedulerSettingsController controller) = Create([OneShot(1)]);
        runtime.Tick(Beginning.AddMinutes(1), TimeZoneInfo.Utc);
        SchedulerOneShotState old = SavedState();
        SchedulerOneShotState[] states = replace
            ? [new SchedulerOneShotState(old.EntryId, old.Date, old.Time,
                Guid.NewGuid(), old.AcceptedAtUtc, SchedulerOneShotStatus.Accepted)] : [];
        controller.Save(controller.Current!.Settings.WithOneShotStates(states));
        byte[] changed = File.ReadAllBytes(SettingsPath);
        Assert.Null(runtime.TryTake());
        Assert.False(runtime.IsActive);
        Assert.Equal(0, runtime.RunningCount);
        Assert.Equal(changed, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void ReportingResultDoesNotReleaseRunningRow()
    {
        (SchedulerRuntime runtime, _) = Create([SchedulerSettingsTests.Entry()]);
        SchedulerExecutionRequest request = Assert.IsType<SchedulerExecutionRequest>(runtime.TryTake());
        Assert.False(runtime.ReleaseRecovered(request.OccurrenceId));
        Assert.True(runtime.ReportResult(request.OccurrenceId,
            SchedulerExecutionResult.Unknown(true, null, false)));
        Assert.Equal(1, runtime.RunningCount);
        Assert.False(runtime.ReportResult(request.OccurrenceId,
            SchedulerExecutionResult.Completed(0, true, false, true)));
        Assert.Equal(1, runtime.RunningCount);
        Assert.False(runtime.ReleaseRecovered(Guid.NewGuid()));
        Assert.True(runtime.ReleaseRecovered(request.OccurrenceId));
        Assert.Equal(0, runtime.RunningCount);
        Assert.False(runtime.ReleaseRecovered(request.OccurrenceId));
    }

    private (SchedulerRuntime Runtime, SchedulerSettingsController Controller) Create(IEnumerable<SchedulerEntry?> entries)
    {
        SchedulerSettingsStore.Save(SettingsPath, new SchedulerSettings(entries, enabled: true), null);
        var controller = new SchedulerSettingsController(SettingsPath);
        var runtime = new SchedulerRuntime(controller, () => true);
        Assert.True(runtime.Start(Beginning, TimeZoneInfo.Utc));
        return (runtime, controller);
    }

    private static SchedulerEntry OneShot(int minute) => SchedulerSettingsTests.Entry(
        new SchedulerOneShotCondition(DateOnly.FromDateTime(Beginning.DateTime),
            TimeOnly.FromDateTime(Beginning.DateTime.AddMinutes(minute))));

    private SchedulerOneShotState SavedState() => Assert.Single(
        SchedulerSettingsStore.Load(SettingsPath).Settings.OneShotStates);

    public void Dispose()
    {
        string target = Path.GetFullPath(directory);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(target) != temp || !Path.GetFileName(target).StartsWith("uwsc-schedule-runtime-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("検査用フォルダーの範囲が一致しません。");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, true);
        }
    }
}
