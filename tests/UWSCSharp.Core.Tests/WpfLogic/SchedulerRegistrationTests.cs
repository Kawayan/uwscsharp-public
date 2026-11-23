using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerRegistrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-schedule-registration-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now =
        new(2027, 1, 1, 18, 4, 59, 999, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("RegistrationUtc9",
            TimeSpan.FromHours(9), "RegistrationUtc9", "RegistrationUtc9");

    private string SettingsPath => Path.Combine(directory, "schedule.json");

    [Theory]
    [InlineData(-1440)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1440)]
    public void NewOneShotRequiresLaterLocalMinute(int minuteOffset)
    {
        SchedulerSettingsController controller = Load(new SchedulerSettings());
        SchedulerEntry entry = Entry(minuteOffset);
        if (minuteOffset <= 0)
        {
            AssertRejectedWithoutWrite(controller, () =>
                controller.SaveEntry(0, entry, Now, Zone));
            return;
        }

        controller.SaveEntry(0, entry, Now, Zone);
        SchedulerSettings saved = SchedulerSettingsStore.Load(SettingsPath).Settings;
        Assert.Equal(entry.Id, saved.Slots[0]!.Id);
        Assert.Empty(saved.OneShotStates);
    }

    [Fact]
    public void EditingPastMetadataAndEnabledKeepsConsumption()
    {
        SchedulerEntry entry = Entry(-1);
        SchedulerOneShotState state = State(entry);
        SchedulerSettingsController controller = Load(
            new SchedulerSettings([entry], oneShotStates: [state]));
        var edited = new SchedulerEntry(entry.Id, "変更した名前",
            "different.uws", ["", "順序付き"],
            SchedulerExecutionMode.SameProcess, entry.Condition,
            directory, enabled: false);
        controller.SaveEntry(0, edited, Now, Zone);
        SchedulerSettings saved = SchedulerSettingsStore.Load(SettingsPath).Settings;
        Assert.Equal(state.OccurrenceId,
            Assert.Single(saved.OneShotStates).OccurrenceId);
        Assert.False(saved.Slots[0]!.Enabled);
        var enabled = new SchedulerEntry(edited.Id, edited.Title, edited.Path,
            edited.Arguments, edited.Mode, edited.Condition, directory);
        controller.SaveEntry(0, enabled, Now, Zone);
        Assert.Equal(state.OccurrenceId, Assert.Single(
            controller.Current!.Settings.OneShotStates).OccurrenceId);
    }

    [Fact]
    public void CopyingPastOneShotIsNewRegistration()
    {
        SchedulerEntry entry = Entry(-1);
        SchedulerSettingsController controller = Load(
            new SchedulerSettings([entry], oneShotStates: [State(entry)]));
        AssertRejectedWithoutWrite(controller, () =>
            controller.SaveEntry(1, entry.Copy(), Now, Zone));
    }

    [Fact]
    public void CopyingFutureOneShotDoesNotInheritConsumption()
    {
        SchedulerEntry entry = Entry(1);
        SchedulerSettingsController controller = Load(new SchedulerSettings(
            [entry], oneShotStates: [State(entry)]));
        SchedulerEntry copy = entry.Copy();
        controller.SaveEntry(1, copy, Now, Zone);
        SchedulerSettings saved = SchedulerSettingsStore.Load(SettingsPath).Settings;
        Assert.Equal(entry.Id, Assert.Single(saved.OneShotStates).EntryId);
        Assert.Equal(copy.Id, saved.Slots[1]!.Id);
        Assert.NotEqual(entry.Id, copy.Id);
    }

    [Fact]
    public void ChangingStartupToCurrentOneShotIsRejected()
    {
        SchedulerEntry entry = SchedulerSettingsTests.Entry();
        SchedulerSettingsController controller = Load(
            new SchedulerSettings([entry]));
        SchedulerEntry changed = SchedulerSettingsTests.Entry(
            Entry(0).Condition, entry.Id);
        AssertRejectedWithoutWrite(controller, () =>
            controller.SaveEntry(0, changed, Now, Zone));
    }

    [Fact]
    public void ChangingDateClearsOnlyThatRowsConsumption()
    {
        SchedulerEntry first = Entry(-1);
        SchedulerEntry other = Entry(1);
        SchedulerSettingsController controller = Load(new SchedulerSettings(
            [first, other], oneShotStates: [State(first), State(other)]));
        SchedulerEntry changed = SchedulerSettingsTests.Entry(
            Entry(1).Condition, first.Id);
        controller.SaveEntry(0, changed, Now, Zone);
        SchedulerSettings saved = SchedulerSettingsStore.Load(SettingsPath).Settings;
        Assert.Equal(other.Id, Assert.Single(saved.OneShotStates).EntryId);
        Assert.Equal(first.Id, saved.Slots[0]!.Id);
        AssertRejectedWithoutWrite(controller, () =>
            controller.SaveEntry(0, first, Now, Zone));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ExplicitRearmRequiresFutureAndKeepsOtherRows(int minuteOffset)
    {
        SchedulerEntry first = Entry(minuteOffset);
        SchedulerEntry other = Entry(1);
        SchedulerSettingsController controller = Load(new SchedulerSettings(
            [first, other], oneShotStates: [State(first), State(other)]));
        if (minuteOffset <= 0)
        {
            AssertRejectedWithoutWrite(controller, () =>
                controller.RearmEntry(0, Now, Zone));
            return;
        }

        controller.RearmEntry(0, Now, Zone);
        SchedulerSettings saved = SchedulerSettingsStore.Load(SettingsPath).Settings;
        Assert.Equal(other.Id, Assert.Single(saved.OneShotStates).EntryId);
        Assert.Equal(first.Id, saved.Slots[0]!.Id);
    }

    [Fact]
    public void NonOneShotAndEmptySlotsCannotBeRearmed()
    {
        SchedulerSettingsController controller = Load(new SchedulerSettings(
            [SchedulerSettingsTests.Entry()]));
        AssertRejectedWithoutWrite(controller, () =>
            controller.RearmEntry(0, Now, Zone));
        AssertRejectedWithoutWrite(controller, () =>
            controller.RearmEntry(1, Now, Zone));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            controller.RearmEntry(-1, Now, Zone));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            controller.RearmEntry(40, Now, Zone));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleEditorCannotOverwriteConcurrentSave(bool rearm)
    {
        SchedulerEntry entry = Entry(1);
        SchedulerSettingsController controller = Load(new SchedulerSettings(
            [entry], oneShotStates: [State(entry)]));
        SchedulerSettingsSnapshot original = controller.Current!;
        SchedulerSettingsStore.Save(SettingsPath,
            original.Settings.WithOptions(true, true, true), original.Revision);
        byte[] bytes = File.ReadAllBytes(SettingsPath);
        Assert.Throws<SchedulerSettingsConflictException>(() =>
        {
            if (rearm)
            {
                controller.RearmEntry(0, Now, Zone);
            }
            else
            {
                controller.SaveEntry(1, Entry(2), Now, Zone);
            }
        });
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
        Assert.Same(original, controller.Current);
    }

    [Fact]
    public void FailedReloadBlocksEditingAndRearming()
    {
        SchedulerSettingsController controller = Load(new SchedulerSettings());
        File.WriteAllText(SettingsPath, "{");
        Assert.Throws<InvalidDataException>(controller.Reload);
        Assert.Throws<InvalidOperationException>(() =>
            controller.SaveEntry(0, Entry(1), Now, Zone));
        Assert.Throws<InvalidOperationException>(() =>
            controller.RearmEntry(0, Now, Zone));
        Assert.Equal("{", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void RearmDoesNotHandOffStaleReceiptAndCanAcceptNewOccurrence()
    {
        SchedulerEntry entry = Entry(1);
        SchedulerSettingsController controller = Load(
            new SchedulerSettings([entry], enabled: true));
        var runtime = new SchedulerRuntime(controller, () => true);
        Assert.True(runtime.Start(Now, Zone));
        runtime.Tick(Now.AddMinutes(1), Zone);
        Guid old = Assert.Single(
            controller.Current!.Settings.OneShotStates).OccurrenceId;
        controller.RearmEntry(0, Now, Zone);
        Assert.Null(runtime.TryTake());
        Assert.False(runtime.IsActive);
        Assert.Empty(controller.Current.Settings.OneShotStates);
        Assert.True(runtime.Start(Now, Zone));
        runtime.Tick(Now.AddMinutes(1), Zone);
        Assert.Equal(0, runtime.PendingCount);
        controller.SaveEntry(0, SchedulerSettingsTests.Entry(
            Entry(2).Condition, entry.Id), Now.AddMinutes(1), Zone);
        runtime.Tick(Now.AddMinutes(2), Zone);
        SchedulerExecutionRequest request = Assert.IsType<SchedulerExecutionRequest>(
            runtime.TryTake());
        Assert.NotEqual(old, request.OccurrenceId);
        runtime.Complete(request.OccurrenceId, succeeded: true);
        Assert.Equal(SchedulerOneShotStatus.Succeeded, Assert.Single(
            controller.Current.Settings.OneShotStates).Status);
    }

    private SchedulerSettingsController Load(SchedulerSettings settings)
    {
        SchedulerSettingsStore.Save(SettingsPath, settings, null);
        var controller = new SchedulerSettingsController(SettingsPath);
        controller.Reload();
        return controller;
    }

    private static SchedulerEntry Entry(int minutes)
    {
        DateTime target = SchedulerCalendar.LocalMinute(Now, Zone).AddMinutes(minutes);
        return SchedulerSettingsTests.Entry(new SchedulerOneShotCondition(
            DateOnly.FromDateTime(target), TimeOnly.FromDateTime(target)));
    }

    private static SchedulerOneShotState State(SchedulerEntry entry)
    {
        var condition = (SchedulerOneShotCondition)entry.Condition;
        return new SchedulerOneShotState(entry.Id, condition.Date,
            condition.Time, Guid.NewGuid(), Now,
            SchedulerOneShotStatus.Succeeded);
    }

    private void AssertRejectedWithoutWrite(
        SchedulerSettingsController controller, Action operation)
    {
        SchedulerSettingsSnapshot snapshot = controller.Current!;
        byte[] bytes = File.ReadAllBytes(SettingsPath);
        Assert.Throws<InvalidDataException>(operation);
        Assert.Same(snapshot, controller.Current);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
    }

    public void Dispose()
    {
        string target = Path.GetFullPath(directory);
        string temp = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(target) != temp
            || !Path.GetFileName(target).StartsWith(
                "uwsc-schedule-registration-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("検査用フォルダーの範囲が一致しません。");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, true);
        }
    }
}
