using System.IO;

namespace UWSCSharp.Wpf;

/// <summary>
/// 最後の正常値を保持し、読込み失敗後の新規受付を止めます。
/// </summary>
internal sealed class SchedulerSettingsController
{
    private readonly string path;
    private readonly Func<int>? initialCapacity;

    internal SchedulerSettingsController(string? path = null, Func<int>? initialCapacity = null)
    {
        this.path = path ?? ApplicationInfo.SettingsPath("schedule.json");
        this.initialCapacity = initialCapacity;
    }

    internal SchedulerSettingsSnapshot? Current
    {
        get; private set;
    }

    internal Exception? LoadError
    {
        get; private set;
    }

    internal bool CanAcceptNewReservations =>
        LoadError is null && Current?.Settings.Enabled == true;

    internal void Reload()
    {
        try
        {
            Current = SchedulerSettingsStore.LoadWithInitialCapacity(path, initialCapacity);
            LoadError = null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException
            or UnauthorizedAccessException or ArgumentException)
        {
            LoadError = error;
            throw;
        }
    }

    /// <summary>
    /// 復元・実行状態の保存です。登録編集にはSaveEntryを使います。
    /// </summary>
    internal void Save(SchedulerSettings candidate)
    {
        SchedulerSettingsSnapshot snapshot = CurrentForSave();
        Current = SchedulerSettingsStore.Save(
            path, candidate, snapshot.Revision);
    }

    internal SchedulerRegistrationDraft BeginEdit() => new(CurrentForSave());

    internal void CommitEdit(SchedulerRegistrationDraft draft,
        DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (CurrentForSave().Revision != draft.Revision)
        {
            throw new SchedulerSettingsConflictException();
        }
        SchedulerSettings candidate = draft.Build(instant, timeZone);
        Current = SchedulerSettingsStore.Save(path, candidate, draft.Revision);
    }

    /// <summary>
    /// 新規登録と日時変更では現在の分より後を要求します。
    /// </summary>
    internal void SaveEntry(int index, SchedulerEntry entry,
        DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(entry);
        SchedulerSettings settings = CurrentForSave().Settings;
        SchedulerSettings candidate = settings.WithSlot(index, entry);
        SchedulerEntry? previous = settings.Slots[index];
        if (entry.Condition is SchedulerOneShotCondition oneShot
            && (previous?.Id != entry.Id
                || previous.Condition is not SchedulerOneShotCondition old
                || old.Date != oneShot.Date || old.Time != oneShot.Time))
        {
            RequireFuture(oneShot, instant, timeZone);
        }

        Save(candidate);
    }

    /// <summary>
    /// 未来の一回予約だけを明示的に未消費へ戻します。
    /// </summary>
    internal void RearmEntry(int index, DateTimeOffset instant,
        TimeZoneInfo timeZone)
    {
        SchedulerSettings settings = CurrentForSave().Settings;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            index, settings.Slots.Count);
        SchedulerEntry? entry = settings.Slots[index];
        if (entry?.Condition is not SchedulerOneShotCondition oneShot)
        {
            throw new InvalidDataException(
                "再予約する一回予約を選択してください。");
        }

        RequireFuture(oneShot, instant, timeZone);
        Save(settings.WithOneShotStates(settings.OneShotStates.Where(
            state => state.EntryId != entry.Id)));
    }

    private SchedulerSettingsSnapshot CurrentForSave()
    {
        if (LoadError is not null || Current is null)
        {
            throw new InvalidOperationException("予約設定を再読込みするか退避して初期化してください。");
        }

        return Current;
    }

    internal static void RequireFuture(SchedulerOneShotCondition condition,
        DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        DateTime minute = SchedulerCalendar.LocalMinute(instant, timeZone);
        if (condition.Date.ToDateTime(condition.Time) <= minute)
        {
            throw new InvalidDataException(
                "新しい一回予約と再予約は現在の分より後に指定してください。");
        }
    }

    internal string? ResetWithBackup()
    {
        (SchedulerSettingsSnapshot snapshot, string? backup) =
            SchedulerSettingsStore.ResetWithBackup(path);
        Current = snapshot;
        LoadError = null;
        return backup;
    }
}
