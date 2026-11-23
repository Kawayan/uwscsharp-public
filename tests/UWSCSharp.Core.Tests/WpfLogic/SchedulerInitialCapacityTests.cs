using UWSCSharp.IO;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerInitialCapacityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-initial-capacity-" + Guid.NewGuid());

    public SchedulerInitialCapacityTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public async Task OnlyMissingJsonUsesIniAndSavingPreservesReservations()
    {
        string path = Path.Combine(directory, "schedule.json");
        string ini = Path.Combine(directory, "UWSC.INI");
        await new ApplicationScheduleSettings(80).SaveAsync(ini);
        var controller = new SchedulerSettingsController(path,
            () => ApplicationScheduleSettings.LoadAsync(ini).AsTask().GetAwaiter().GetResult().Capacity);
        controller.Reload();
        Assert.Equal(80, controller.Current!.Settings.Slots.Count);
        Assert.False(controller.CanAcceptNewReservations);
        Assert.False(File.Exists(path));
        SchedulerEntry entry = SchedulerSettingsTests.Entry();
        controller.Save(controller.Current.Settings.WithSlot(79, entry));
        byte[] saved = await File.ReadAllBytesAsync(path);
        await new ApplicationScheduleSettings(40).SaveAsync(ini);
        controller.Reload();
        Assert.Equal(80, controller.Current!.Settings.Slots.Count);
        Assert.Equal(entry.Id, controller.Current.Settings.Slots[79]!.Id);
        Assert.Equal(saved, await File.ReadAllBytesAsync(path));
        File.Delete(ini);
        Directory.CreateDirectory(ini);
        controller.Reload();
        Assert.Equal(80, controller.Current!.Settings.Slots.Count);
        Assert.Null(controller.LoadError);
    }

    [Fact]
    public void CorruptJsonNeverFallsBackAndMissingIniFailureBlocksSaving()
    {
        string path = Path.Combine(directory, "schedule.json");
        int reads = 0;
        var controller = new SchedulerSettingsController(path, () =>
        {
            reads++;
            throw new IOException("INI読込み失敗");
        });
        File.WriteAllText(path, "{");
        Assert.ThrowsAny<Exception>(controller.Reload);
        Assert.Equal(0, reads);
        Assert.False(controller.CanAcceptNewReservations);
        File.Delete(path);
        Assert.Throws<IOException>(controller.Reload);
        Assert.Equal(1, reads);
        Assert.Throws<InvalidOperationException>(() => controller.Save(new SchedulerSettings()));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void InitialDraftDoesNotBypassRevisionConflictOrResetRules()
    {
        string path = Path.Combine(directory, "schedule.json");
        var first = new SchedulerSettingsController(path, () => 80);
        var second = new SchedulerSettingsController(path, () => 200);
        first.Reload();
        second.Reload();
        first.Save(first.Current!.Settings);
        Assert.Throws<SchedulerSettingsConflictException>(() => second.Save(second.Current!.Settings));
        second.Reload();
        Assert.Equal(80, second.Current!.Settings.Slots.Count);
        string? backup = second.ResetWithBackup();
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        Assert.Equal(40, second.Current!.Settings.Slots.Count);
    }
}
