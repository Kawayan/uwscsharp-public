using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerApplicationSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "uwsc-schedule-application-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2027, 1, 2, 3, 0, 0, TimeSpan.Zero);

    private string SettingsPath => Path.Combine(directory, "schedule.json");

    [Fact]
    public void SameApplicationDoesNotReplayStartupAcrossRuntimeReconstruction()
    {
        SchedulerEntry original = SchedulerSettingsTests.Entry();
        SchedulerSettingsStore.Save(SettingsPath, new SchedulerSettings([original], enabled: true), null);
        var session = new SchedulerApplicationSession();
        bool ownsPrevious = true;
        var previous = new SchedulerRuntime(new SchedulerSettingsController(SettingsPath),
            () => ownsPrevious, session);
        Assert.True(previous.Start(Now, TimeZoneInfo.Utc));
        Assert.Equal(1, previous.PendingCount);
        ownsPrevious = false;
        Assert.Null(previous.TryTake());
        Assert.False(previous.IsActive);

        var editor = new SchedulerSettingsController(SettingsPath);
        editor.Reload();
        editor.Save(editor.Current!.Settings.WithSlot(1, SchedulerSettingsTests.Entry()));
        var replacement = new SchedulerRuntime(new SchedulerSettingsController(SettingsPath),
            () => true, session);
        Assert.True(replacement.Start(Now.AddMinutes(1), TimeZoneInfo.Utc));
        Assert.Equal(0, replacement.PendingCount);
        replacement.Stop();
        Assert.True(replacement.Start(Now.AddMinutes(2), TimeZoneInfo.Utc));
        Assert.Equal(0, replacement.PendingCount);

        var nextApplication = new SchedulerRuntime(new SchedulerSettingsController(SettingsPath),
            () => true, new SchedulerApplicationSession());
        Assert.True(nextApplication.Start(Now.AddMinutes(3), TimeZoneInfo.Utc));
        Assert.Equal(2, nextApplication.PendingCount);
    }

    [Fact]
    public void DisabledSettingsDoNotConsumeApplicationStartup()
    {
        SchedulerSettingsStore.Save(SettingsPath, new SchedulerSettings([SchedulerSettingsTests.Entry()]), null);
        var session = new SchedulerApplicationSession();
        var controller = new SchedulerSettingsController(SettingsPath);
        var disabled = new SchedulerRuntime(controller, () => true, session);
        Assert.False(disabled.Start(Now, TimeZoneInfo.Utc));
        Assert.Equal(0, disabled.PendingCount);
        controller.Save(controller.Current!.Settings.WithOptions(true, false, false));
        var enabled = new SchedulerRuntime(new SchedulerSettingsController(SettingsPath), () => true, session);
        Assert.True(enabled.Start(Now.AddMinutes(1), TimeZoneInfo.Utc));
        Assert.Equal(1, enabled.PendingCount);
    }

    [Fact]
    public void FailedValidationDoesNotConsumeApplicationStartup()
    {
        SchedulerSettingsStore.Save(SettingsPath, new SchedulerSettings(
            [SchedulerSettingsTests.Entry(), SchedulerSettingsTests.Entry(new SchedulerExpressionCondition("FOPEN(1)"))],
            enabled: true), null);
        var session = new SchedulerApplicationSession();
        var controller = new SchedulerSettingsController(SettingsPath);
        var invalid = new SchedulerRuntime(controller, () => true, session);
        Assert.False(invalid.Start(Now, TimeZoneInfo.Utc));
        Assert.IsType<InvalidDataException>(invalid.LastError);
        controller.Save(controller.Current!.Settings.WithSlot(1, null));
        var valid = new SchedulerRuntime(new SchedulerSettingsController(SettingsPath), () => true, session);
        Assert.True(valid.Start(Now.AddMinutes(1), TimeZoneInfo.Utc));
        Assert.Equal(1, valid.PendingCount);
    }

    [Fact]
    public async Task ConcurrentRuntimeStartsAcceptStartupOnlyOnceForSharedApplication()
    {
        SchedulerSettingsStore.Save(SettingsPath, new SchedulerSettings(
            [SchedulerSettingsTests.Entry()], enabled: true), null);
        var session = new SchedulerApplicationSession();
        using var start = new ManualResetEventSlim();
        Task<SchedulerRuntime>[] tasks = [.. Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            var runtime = new SchedulerRuntime(new SchedulerSettingsController(SettingsPath), () => true, session);
            start.Wait();
            Assert.True(runtime.Start(Now, TimeZoneInfo.Utc));
            return runtime;
        }))];
        start.Set();
        SchedulerRuntime[] runtimes = await Task.WhenAll(tasks);
        Assert.Equal(1, runtimes.Sum(runtime => runtime.PendingCount));
        foreach (SchedulerRuntime runtime in runtimes)
        {
            runtime.Stop();
        }
    }

    public void Dispose()
    {
        string target = Path.GetFullPath(directory);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(target) != temp || !Path.GetFileName(target).StartsWith("uwsc-schedule-application-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("検査用フォルダーの範囲が一致しません。");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, true);
        }
    }
}
