using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerMonitorLeaseTests
{
    [Fact]
    public void RuntimeStopsWithoutSavingAfterActualFileOwnershipIsLost()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-monitor-loss-" + Guid.NewGuid().ToString("N"));
        string leasePath = Path.Combine(directory, "schedule.monitor");
        string settingsPath = Path.Combine(directory, "schedule.json");
        try
        {
            using SchedulerMonitorLease first = Assert.IsType<SchedulerMonitorLease>(
                SchedulerMonitorLease.TryAcquire(leasePath));
            SchedulerEntry entry = SchedulerSettingsTests.Entry(
                new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 1));
            SchedulerSettingsStore.Save(settingsPath,
                new SchedulerSettings([entry], enabled: true), null);
            var controller = new SchedulerSettingsController(settingsPath);
            var runtime = new SchedulerRuntime(controller, () => first.IsActive);
            var now = new DateTimeOffset(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);
            Assert.True(runtime.Start(now, TimeZoneInfo.Utc));
            byte[] before = File.ReadAllBytes(settingsPath);
            first.Handle.Dispose();
            using SchedulerMonitorLease successor = Assert.IsType<SchedulerMonitorLease>(
                SchedulerMonitorLease.TryAcquire(leasePath));
            runtime.Tick(now.AddMinutes(1), TimeZoneInfo.Utc);
            Assert.False(runtime.IsActive);
            Assert.NotNull(runtime.LastError);
            Assert.Equal(0, runtime.PendingCount);
            Assert.Null(runtime.TryTake());
            Assert.Equal(before, File.ReadAllBytes(settingsPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedNativeHandleRevokesOwnershipAndAllowsAnotherLease()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-monitor-lease-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "schedule.monitor");
        try
        {
            using SchedulerMonitorLease first = Assert.IsType<SchedulerMonitorLease>(
                SchedulerMonitorLease.TryAcquire(path));
            Assert.True(first.IsActive);
            first.Handle.Dispose();
            using SchedulerMonitorLease second = Assert.IsType<SchedulerMonitorLease>(
                SchedulerMonitorLease.TryAcquire(path));
            Assert.True(second.IsActive);
            Assert.False(first.IsActive);
            Assert.Throws<ObjectDisposedException>(() => first.Handle);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
