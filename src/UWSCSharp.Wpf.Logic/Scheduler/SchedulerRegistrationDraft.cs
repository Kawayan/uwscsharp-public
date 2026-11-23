using System.IO;

namespace UWSCSharp.Wpf;

/// <summary>
/// 登録画面の原稿を保持し、適用時に一回予約と競合を検査します。
/// </summary>
internal sealed class SchedulerRegistrationDraft(SchedulerSettingsSnapshot original)
{
    private readonly HashSet<Guid> rearmed = [];

    internal Guid? Revision => original.Revision;

    internal SchedulerSettings Settings
    {
        get; private set;
    } = original.Settings;

    internal void SetEntry(int index, SchedulerEntry? entry) =>
        Settings = Settings.WithSlot(index, entry);

    internal void Move(int from, int to) =>
        Settings = Settings.MoveSlot(from, to);

    internal void Copy(int from, int to)
    {
        SchedulerEntry entry = Settings.Slots[from]
            ?? throw new InvalidDataException("コピーする予約を選択してください。");
        if (Settings.Slots[to] is not null)
        {
            throw new InvalidDataException("コピー先には空き枠を選択してください。");
        }
        Settings = Settings.WithSlot(to, entry.Copy());
    }

    internal void Resize(int capacity) => Settings = Settings.Resize(capacity);

    internal void SetEnabled(bool enabled) =>
        Settings = Settings.WithOptions(enabled, Settings.Beep, Settings.WakeUp);

    internal void SetBeep(bool beep) =>
        Settings = Settings.WithOptions(Settings.Enabled, beep, Settings.WakeUp);

    internal void SetWakeUp(bool wakeUp) =>
        Settings = Settings.WithOptions(Settings.Enabled, Settings.Beep, wakeUp);

    internal void Rearm(int index)
    {
        SchedulerEntry? entry = Settings.Slots[index];
        if (entry?.Condition is not SchedulerOneShotCondition)
        {
            throw new InvalidDataException("再予約する一回予約を選択してください。");
        }
        rearmed.Add(entry.Id);
    }

    internal bool IsRearmed(Guid id) => rearmed.Contains(id);

    internal SchedulerSettings Build(DateTimeOffset instant, TimeZoneInfo timeZone)
    {
        var previous = original.Settings.Slots.Where(entry => entry is not null)
            .ToDictionary(entry => entry!.Id, entry => entry!);
        foreach (SchedulerEntry? entry in Settings.Slots)
        {
            if (entry?.Condition is SchedulerOneShotCondition oneShot
                && (rearmed.Contains(entry.Id)
                    || !previous.TryGetValue(entry.Id, out SchedulerEntry? old)
                    || old.Condition is not SchedulerOneShotCondition before
                    || before.Date != oneShot.Date || before.Time != oneShot.Time))
            {
                SchedulerSettingsController.RequireFuture(oneShot, instant, timeZone);
            }
        }
        // 一時的な削除・変更を経ても、最終的に同じ予約なら元の消費を保持します。
        SchedulerOneShotState[] states = [.. original.Settings.OneShotStates.Where(state =>
            !rearmed.Contains(state.EntryId) && state.AppliesTo(Settings.Slots
                .FirstOrDefault(entry => entry?.Id == state.EntryId)))];
        var candidate = new SchedulerSettings(Settings.Slots, Settings.Slots.Count,
            Settings.Enabled, Settings.Beep, Settings.WakeUp, states);
        _ = new SchedulerCalendar(candidate);
        return candidate;
    }
}
