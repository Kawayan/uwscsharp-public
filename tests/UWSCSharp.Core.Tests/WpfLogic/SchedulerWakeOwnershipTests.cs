using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerWakeOwnershipTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2027, 2, 1, 10, 0, 20, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "uwsc-wake-owner-" + Guid.NewGuid().ToString("N"));
    private readonly SchedulerMonitorSession session;
    private readonly List<Resource> resources = [];
    private readonly List<SchedulerDispatch> jobs = [];
    private SchedulerMonitorLease? lease;
    private string SettingsPath => Path.Combine(directory, "schedule.json");
    private Resource Current => resources[^1];

    public SchedulerWakeOwnershipTests()
    {
        Directory.CreateDirectory(directory);
        SchedulerSettingsStore.Save(SettingsPath, new SchedulerSettings(
            [SchedulerSettingsTests.Entry(), SchedulerSettingsTests.Entry(new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 5))],
            enabled: true, wakeUp: true), null);
        var gate = new SchedulerExecutionGate();
        gate.SetInputCleanupConfirmed(true);
        session = new(new(SettingsPath), new(), gate, Path.Combine(directory, "monitor"), owned =>
        {
            lease = owned;
            var resource = new Resource();
            resources.Add(resource);
            return resource;
        });
    }

    public async Task InitializeAsync() => Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));

    [Fact]
    public void UnchangedSnapshotReusesTicketAndArmsOneMinuteEarly()
    {
        SchedulerWakeRequest request = Capture();
        Assert.Same(request, Capture());
        var due = new DateTimeOffset(2027, 2, 1, 10, 5, 0, TimeSpan.Zero);
        Assert.True(session.ApplyWakePlan(request, new(due), Now, TimeZoneInfo.Utc));
        Assert.Equal(due.AddMinutes(-1), Current.ArmedAt);
        Assert.Null(session.WakeError);
    }

    [Fact]
    public void NearDueTimeDoesNotRegisterPastLeadTime()
    {
        var due = new DateTimeOffset(2027, 2, 1, 10, 1, 0, TimeSpan.Zero);
        Assert.True(session.ApplyWakePlan(Capture(), new(due), Now, TimeZoneInfo.Utc));
        Assert.Equal(due, Current.ArmedAt);
    }

    [Fact]
    public void SettingsSaveRejectsOldResultAndCancelsPreviousTimer()
    {
        SchedulerWakeRequest old = Capture();
        session.ApplyWakePlan(old, new(Now.AddMinutes(5)), Now, TimeZoneInfo.Utc);
        Save(settings => settings.WithOptions(true, true, true));
        Assert.False(session.ApplyWakePlan(old, new(Now.AddMinutes(6)), Now, TimeZoneInfo.Utc));
        Assert.Null(Current.ArmedAt);
        Assert.NotSame(old, Capture());
    }

    [Fact]
    public void SameRevisionCalendarEditRejectsOldResult()
    {
        SchedulerWakeRequest old = Capture();
        session.ApplyWakePlan(old, new(Now.AddMinutes(5)), Now, TimeZoneInfo.Utc);
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        root["slots"]![1]!["condition"]!["value"] = 10;
        File.WriteAllText(SettingsPath, root.ToJsonString());
        Assert.Equal(old.Revision, SchedulerSettingsStore.Load(SettingsPath).Revision);
        Assert.False(session.ApplyWakePlan(old, new(Now.AddMinutes(6)), Now, TimeZoneInfo.Utc));
        Assert.Null(Current.ArmedAt);
    }

    [Fact]
    public void NewMinuteRejectsOldResult()
    {
        SchedulerWakeRequest old = Capture();
        Assert.False(session.ApplyWakePlan(old, new(Now.AddMinutes(6)), Now.AddMinutes(1), TimeZoneInfo.Utc));
        Assert.Null(Current.ArmedAt);
    }

    [Fact]
    public void ClockRewindRefreshesInstantWithoutReplayingWatermark()
    {
        SchedulerWakeRequest old = Capture();
        SchedulerWakeRequest rewound = Assert.IsType<SchedulerWakeRequest>(
            session.BeginWakePlanning(Now.AddMinutes(-2), TimeZoneInfo.Utc));
        Assert.NotSame(old, rewound);
        Assert.Equal(old.Watermark, rewound.Watermark);
        Assert.Equal(Now.AddMinutes(-2), rewound.Instant);
        Assert.False(session.ApplyWakePlan(old, new(Now.AddMinutes(5)), Now.AddMinutes(-2), TimeZoneInfo.Utc));
    }

    [Fact]
    public void DisabledWakeCancelsWithoutStoppingExecutionMonitor()
    {
        session.ApplyWakePlan(Capture(), new(Now.AddMinutes(5)), Now, TimeZoneInfo.Utc);
        Save(settings => settings.WithOptions(true, false, false));
        Assert.Null(session.BeginWakePlanning(Now, TimeZoneInfo.Utc));
        Assert.Null(Current.ArmedAt);
        Assert.True(session.IsActive);
    }

    [Fact]
    public void StopCancelsWhileExecutionStillOwnsResources()
    {
        session.ApplyWakePlan(Capture(), new(Now.AddMinutes(5)), Now, TimeZoneInfo.Utc);
        jobs.Add(Assert.IsType<SchedulerDispatch>(session.TryDispatch()));
        session.StopAccepting();
        Assert.Null(Current.ArmedAt);
        Assert.True(session.OwnsMonitor);
        Assert.False(Current.Disposed);
    }

    [Fact]
    public async Task StopAndRestartRejectsPreviousGeneration()
    {
        SchedulerWakeRequest old = Capture();
        Resource previous = Current;
        session.StopAccepting();
        Assert.True(previous.Disposed);
        Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
        Assert.False(session.ApplyWakePlan(old, new(Now.AddMinutes(6)), Now, TimeZoneInfo.Utc));
        Assert.Null(Current.ArmedAt);
    }

    [Fact]
    public void UnsupportedWakeDoesNotDisableClockExecution()
    {
        Current.Unsupported = true;
        SchedulerWakeRequest request = Capture();
        Assert.True(session.ApplyWakePlan(request, new(Now.AddMinutes(5)), Now, TimeZoneInfo.Utc));
        Assert.IsType<NotSupportedException>(session.WakeError);
        Assert.True(session.IsActive);
        Assert.Same(request, Capture());
        jobs.Add(Assert.IsType<SchedulerDispatch>(session.TryDispatch()));
    }

    [Fact]
    public async Task CancelFailureRetainsLeaseUntilExplicitPreparation()
    {
        session.ApplyWakePlan(Capture(), new(Now.AddMinutes(5)), Now, TimeZoneInfo.Utc);
        Current.FailCancel = true;
        session.StopAccepting();
        Assert.True(session.OwnsMonitor);
        Assert.IsType<IOException>(session.WakeError);
        Current.FailCancel = false;
        Assert.True(await session.TryStartAsync(Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ChangedZoneStopsAndRejectsResult()
    {
        SchedulerWakeRequest old = Capture();
        var zone = TimeZoneInfo.CreateCustomTimeZone("wake-owner-zone", TimeSpan.FromHours(1), "wake-owner-zone", "wake-owner-zone");
        Assert.False(session.ApplyWakePlan(old, new(Now.AddMinutes(5)), Now, zone));
        Assert.False(session.IsActive);
        Assert.Null(Current.ArmedAt);
    }

    [Fact]
    public void LostFileHandleCancelsAndRejectsResult()
    {
        SchedulerWakeRequest old = Capture();
        session.ApplyWakePlan(old, new(Now.AddMinutes(5)), Now, TimeZoneInfo.Utc);
        lease!.Handle.Dispose();
        Assert.False(session.ApplyWakePlan(old, new(Now.AddMinutes(6)), Now, TimeZoneInfo.Utc));
        Assert.Null(Current.ArmedAt);
        Assert.True(Current.Disposed);
    }

    private SchedulerWakeRequest Capture() => Assert.IsType<SchedulerWakeRequest>(session.BeginWakePlanning(Now, TimeZoneInfo.Utc));

    private void Save(Func<SchedulerSettings, SchedulerSettings> update)
    {
        var editor = new SchedulerSettingsController(SettingsPath);
        editor.Reload();
        editor.Save(update(editor.Current!.Settings));
    }

    public Task DisposeAsync()
    {
        foreach (Resource resource in resources)
        {
            resource.FailCancel = false;
        }
        foreach (SchedulerDispatch job in jobs)
        {
            session.Complete(job, succeeded: true, resourcesRecovered: true);
        }
        session.StopAccepting();
        session.Dispose();
        string target = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(target) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(target).StartsWith("uwsc-wake-owner-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("復帰監視テストの回収先が範囲外です。");
        }
        Directory.Delete(target, recursive: true);
        return Task.CompletedTask;
    }

    private sealed class Resource : ISchedulerMonitorResources, ISchedulerWakeResources
    {
        internal bool Unsupported
        {
            get; set;
        }
        internal bool FailCancel
        {
            get; set;
        }
        internal bool Disposed
        {
            get; private set;
        }
        internal DateTimeOffset? ArmedAt
        {
            get; private set;
        }
        public bool CanRelease => true;

        public Task PrepareAsync(CancellationToken token) => Task.CompletedTask;

        public bool UpdateWake(DateTimeOffset? dueAt)
        {
            if (dueAt is null && FailCancel)
            {
                throw new IOException("取消失敗の注入");
            }
            ArmedAt = Unsupported ? null : dueAt;
            return dueAt is null || !Unsupported;
        }

        public void Dispose()
        {
            Disposed = true;
            ArmedAt = null;
        }
    }
}
