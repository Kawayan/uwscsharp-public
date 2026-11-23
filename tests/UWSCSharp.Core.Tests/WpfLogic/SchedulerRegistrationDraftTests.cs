using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 編集原稿と一時設定だけを使い、画面・入力・実時計の変更は行いません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerRegistrationDraftTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-registration-draft-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2027, 1, 1, 18, 4, 59, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone(
        "draft-zone", TimeSpan.FromHours(9), "draft-zone", "draft-zone");
    private string SettingsPath => Path.Combine(directory, "schedule.json");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WakeOptionPreservesOtherSettingsUntilSave(bool wakeUp)
    {
        SchedulerSettingsController controller = Load(new SchedulerSettings(
            [Entry(1)], enabled: true, beep: true, wakeUp: !wakeUp));
        var draft = new SchedulerRegistrationDraft(controller.Current!);
        draft.SetWakeUp(wakeUp);
        Assert.True(draft.Settings.Enabled);
        Assert.True(draft.Settings.Beep);
        Assert.Equal(wakeUp, draft.Settings.WakeUp);
        Assert.Equal(!wakeUp, SchedulerSettingsStore.Load(SettingsPath).Settings.WakeUp);
        controller.Save(draft.Settings);
        Assert.Equal(wakeUp, SchedulerSettingsStore.Load(SettingsPath).Settings.WakeUp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoundOptionPreservesOtherSettingsAndRequiresSave(bool beep)
    {
        SchedulerSettingsController controller = Load(new SchedulerSettings(
            [Entry(1)], enabled: true, beep: !beep, wakeUp: true));
        var draft = new SchedulerRegistrationDraft(controller.Current!);
        draft.SetBeep(beep);
        Assert.True(draft.Settings.Enabled);
        Assert.True(draft.Settings.WakeUp);
        Assert.Equal(beep, draft.Settings.Beep);
        Assert.Equal(!beep, SchedulerSettingsStore.Load(SettingsPath).Settings.Beep);
        controller.Save(draft.Settings);
        Assert.Equal(beep, SchedulerSettingsStore.Load(SettingsPath).Settings.Beep);
    }

    private SchedulerSettingsController Load(SchedulerSettings settings)
    {
        Directory.CreateDirectory(directory);
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
        return new(entry.Id, condition.Date, condition.Time, Guid.NewGuid(), Now,
            SchedulerOneShotStatus.Succeeded);
    }

    [Fact]
    public void DraftDoesNotWriteUntilAtomicCommitAndKeepsOptionsAndArguments()
    {
        SchedulerSettingsController controller = Load(new SchedulerSettings(beep: true, wakeUp: true));
        byte[] before = File.ReadAllBytes(SettingsPath);
        SchedulerRegistrationDraft draft = controller.BeginEdit();
        SchedulerEntry entry = Entry(1);
        draft.SetEntry(0, new SchedulerEntry(entry.Id, "新規", entry.Path,
            ["", "a b", "日本語"], entry.Mode, entry.Condition, directory));
        draft.Move(0, 2);
        draft.Resize(41);
        draft.SetEnabled(true);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Null(controller.Current!.Settings.Slots[2]);
        controller.CommitEdit(draft, Now, Zone);
        SchedulerSettings result = SchedulerSettingsStore.Load(SettingsPath).Settings;
        Assert.Equal(41, result.Slots.Count);
        Assert.Equal(entry.Id, result.Slots[2]!.Id);
        Assert.Equal(["", "a b", "日本語"], result.Slots[2]!.Arguments);
        Assert.True(result.Enabled && result.Beep && result.WakeUp);
    }

    [Fact]
    public void InvalidSecondRowDoesNotPartiallySaveAndDraftSurvives()
    {
        SchedulerSettingsController controller = Load(new SchedulerSettings());
        SchedulerRegistrationDraft draft = controller.BeginEdit();
        SchedulerEntry first = Entry(1);
        draft.SetEntry(0, first);
        draft.SetEntry(1, Entry(0));
        Reject(controller, () => controller.CommitEdit(draft, Now, Zone));
        Assert.Same(first, draft.Settings.Slots[0]);
        Assert.NotNull(draft.Settings.Slots[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryRemovalOrConditionChangeCannotResetConsumption(bool changeCondition)
    {
        SchedulerEntry entry = Entry(-1);
        SchedulerOneShotState state = State(entry);
        SchedulerSettingsController controller = Load(new SchedulerSettings([entry], oneShotStates: [state]));
        SchedulerRegistrationDraft draft = controller.BeginEdit();
        draft.SetEntry(0, changeCondition ? new SchedulerEntry(entry.Id, entry.Title,
            entry.Path, entry.Arguments, entry.Mode, Entry(2).Condition, directory) : null);
        draft.SetEntry(0, entry);
        draft.Move(0, 4);
        controller.CommitEdit(draft, Now, Zone);
        Assert.Equal(state.OccurrenceId, Assert.Single(controller.Current!.Settings.OneShotStates).OccurrenceId);
        Assert.Equal(entry.Id, controller.Current.Settings.Slots[4]!.Id);
    }

    [Fact]
    public void ExplicitRearmIsStagedAndClearsOnlySelectedFutureEntry()
    {
        SchedulerEntry first = Entry(1);
        SchedulerEntry other = Entry(2);
        SchedulerSettingsController controller = Load(new SchedulerSettings([first, other],
            oneShotStates: [State(first), State(other)]));
        SchedulerRegistrationDraft draft = controller.BeginEdit();
        draft.Rearm(0);
        Assert.True(draft.IsRearmed(first.Id));
        Assert.Equal(2, controller.Current!.Settings.OneShotStates.Count);
        controller.CommitEdit(draft, Now, Zone);
        Assert.Equal(other.Id, Assert.Single(controller.Current.Settings.OneShotStates).EntryId);
    }

    [Fact]
    public void RearmChecksTimeAtCommitRatherThanEditStart()
    {
        SchedulerEntry entry = Entry(1);
        SchedulerSettingsController controller = Load(new SchedulerSettings([entry], oneShotStates: [State(entry)]));
        SchedulerRegistrationDraft draft = controller.BeginEdit();
        draft.Rearm(0);
        Reject(controller, () => controller.CommitEdit(draft, Now.AddMinutes(1), Zone));
        Assert.Single(controller.Current!.Settings.OneShotStates);
    }

    [Fact]
    public void CopyHasNewIdentityAndPastOneShotRequiresNewFutureDate()
    {
        SchedulerEntry entry = Entry(-1);
        SchedulerSettingsController controller = Load(new SchedulerSettings([entry], oneShotStates: [State(entry)]));
        SchedulerRegistrationDraft draft = controller.BeginEdit();
        draft.Copy(0, 1);
        Assert.NotEqual(entry.Id, draft.Settings.Slots[1]!.Id);
        Reject(controller, () => controller.CommitEdit(draft, Now, Zone));
        Assert.Single(controller.Current!.Settings.OneShotStates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnRuntimeUpdateAndExternalSaveBothRejectStaleDraft(bool external)
    {
        SchedulerEntry entry = Entry(1);
        SchedulerSettingsController controller = Load(new SchedulerSettings([entry]));
        SchedulerRegistrationDraft draft = controller.BeginEdit();
        draft.SetEntry(1, Entry(2));
        SchedulerSettings candidate = controller.Current!.Settings.WithOneShotStates([State(entry)]);
        if (external)
        {
            SchedulerSettingsStore.Save(SettingsPath, candidate, controller.Current.Revision);
        }
        else
        {
            controller.Save(candidate);
        }
        byte[] bytes = File.ReadAllBytes(SettingsPath);
        SchedulerSettingsSnapshot snapshot = controller.Current;
        Assert.Throws<SchedulerSettingsConflictException>(() => controller.CommitEdit(draft, Now, Zone));
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
        Assert.Same(snapshot, controller.Current);
        Assert.NotNull(draft.Settings.Slots[1]);
    }

    [Fact]
    public void EnabledExpressionIsValidatedBeforeSave()
    {
        SchedulerSettingsController controller = Load(new SchedulerSettings());
        SchedulerRegistrationDraft draft = controller.BeginEdit();
        SchedulerEntry entry = SchedulerSettingsTests.Entry(new SchedulerExpressionCondition("PRINT(1)"));
        draft.SetEntry(0, entry);
        Reject(controller, () => controller.CommitEdit(draft, Now, Zone));
        draft.SetEntry(0, new SchedulerEntry(entry.Id, entry.Title, entry.Path, entry.Arguments,
            entry.Mode, entry.Condition, directory, enabled: false));
        controller.CommitEdit(draft, Now, Zone);
        Assert.False(controller.Current!.Settings.Slots[0]!.Enabled);
    }

    [Fact]
    public void FailedReloadBlocksNewAndExistingDrafts()
    {
        SchedulerSettingsController controller = Load(new SchedulerSettings());
        SchedulerRegistrationDraft draft = controller.BeginEdit();
        File.WriteAllText(SettingsPath, "{");
        Assert.Throws<InvalidDataException>(controller.Reload);
        Assert.Throws<InvalidOperationException>(() => controller.BeginEdit());
        Assert.Throws<InvalidOperationException>(() => controller.CommitEdit(draft, Now, Zone));
        Assert.Equal("{", File.ReadAllText(SettingsPath));
    }

    private void Reject(SchedulerSettingsController controller, Action action)
    {
        byte[] bytes = File.ReadAllBytes(SettingsPath);
        SchedulerSettingsSnapshot snapshot = controller.Current!;
        Assert.Throws<InvalidDataException>(action);
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
        Assert.Same(snapshot, controller.Current);
    }

    public void Dispose()
    {
        string target = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(target) != Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(target).StartsWith("uwsc-registration-draft-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("検査用フォルダーの範囲が一致しません。");
        }
        if (Directory.Exists(target))
        {
            Directory.Delete(target, true);
        }
    }
}
