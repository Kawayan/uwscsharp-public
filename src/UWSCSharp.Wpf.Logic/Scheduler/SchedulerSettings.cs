using System.IO;
using System.Text;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

internal enum SchedulerExecutionMode
{
    SameProcess,
    SeparateProcess
}

internal sealed class SchedulerEntry
{
    internal SchedulerEntry(Guid id, string title, string path,
        IEnumerable<string> arguments, SchedulerExecutionMode mode,
        SchedulerCondition condition, string baseDirectory, bool enabled = true)
    {
        if (id == Guid.Empty || !Enum.IsDefined(mode))
        {
            throw new InvalidDataException("予約の識別子または実行方式が不正です。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(condition);
        SchedulerValidation.Text(title);
        SchedulerValidation.Text(path);
        SchedulerValidation.Text(baseDirectory);
        string absolute = System.IO.Path.GetFullPath(path, baseDirectory);
        SchedulerValidation.Text(absolute);
        if (!System.IO.Path.GetExtension(absolute).Equals(".uws",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("予約の対象には.uwsファイルを指定してください。");
        }

        var copied = new List<string>();
        foreach (string argument in arguments)
        {
            SchedulerValidation.Text(argument);
            if (copied.Count == UwscArray.MaximumElements)
            {
                throw new InvalidDataException("予約の引数が多すぎます。");
            }

            copied.Add(argument);
        }

        Id = id;
        Title = title;
        Path = absolute;
        Arguments = copied.AsReadOnly();
        Mode = mode;
        Condition = condition;
        Enabled = enabled;
    }

    internal Guid Id
    {
        get;
    }

    internal string Title
    {
        get;
    }

    internal string Path
    {
        get;
    }

    internal string WorkingDirectory => System.IO.Path.GetDirectoryName(Path)!;

    internal IReadOnlyList<string> Arguments
    {
        get;
    }

    internal SchedulerExecutionMode Mode
    {
        get;
    }

    internal SchedulerCondition Condition
    {
        get;
    }

    internal bool Enabled
    {
        get;
    }

    internal SchedulerEntry Copy() => new(Guid.NewGuid(), Title, Path,
        Arguments, Mode, Condition, System.IO.Path.GetPathRoot(Path)!, Enabled);
}

internal sealed class SchedulerSettings
{
    internal const int InitialCapacity = 40;
    internal const int MaximumCapacity = 400;

    internal SchedulerSettings(IEnumerable<SchedulerEntry?>? slots = null,
        int capacity = InitialCapacity, bool enabled = false,
        bool beep = false, bool wakeUp = false,
        IEnumerable<SchedulerOneShotState>? oneShotStates = null)
    {
        if (capacity is < InitialCapacity or > MaximumCapacity)
        {
            throw new InvalidDataException("予約の容量は40〜400枠で指定してください。");
        }

        var copied = new SchedulerEntry?[capacity];
        var ids = new HashSet<Guid>();
        int index = 0;
        foreach (SchedulerEntry? entry in slots ?? [])
        {
            if (index == capacity || entry is not null && !ids.Add(entry.Id))
            {
                throw new InvalidDataException("予約の枠数超過または識別子の重複があります。");
            }

            copied[index++] = entry;
        }

        Slots = Array.AsReadOnly(copied);
        Enabled = enabled;
        Beep = beep;
        WakeUp = wakeUp;
        var states = new List<SchedulerOneShotState>();
        var stateIds = new HashSet<Guid>();
        var occurrences = new HashSet<Guid>();
        foreach (SchedulerOneShotState? state in oneShotStates ?? [])
        {
            if (state is null || states.Count == MaximumCapacity
                || !stateIds.Add(state.EntryId) || !occurrences.Add(state.OccurrenceId)
                || !state.AppliesTo(copied.FirstOrDefault(entry => entry?.Id == state.EntryId)))
            {
                throw new InvalidDataException("一回予約の状態が重複するか登録行と一致しません。");
            }

            states.Add(state);
        }

        OneShotStates = states.AsReadOnly();
    }

    internal IReadOnlyList<SchedulerEntry?> Slots
    {
        get;
    }

    internal bool Enabled
    {
        get;
    }

    internal bool Beep
    {
        get;
    }

    internal bool WakeUp
    {
        get;
    }

    internal IReadOnlyList<SchedulerOneShotState> OneShotStates
    {
        get;
    }

    internal SchedulerSettings WithOptions(bool enabled, bool beep, bool wakeUp) =>
        new(Slots, Slots.Count, enabled, beep, wakeUp, OneShotStates);

    internal SchedulerSettings WithOneShotStates(IEnumerable<SchedulerOneShotState> states) =>
        new(Slots, Slots.Count, Enabled, Beep, WakeUp, states);

    internal SchedulerSettings WithSlot(int index, SchedulerEntry? entry)
    {
        ValidateIndex(index);
        SchedulerEntry?[] copy = [.. Slots];
        copy[index] = entry;
        return Rebuild(copy);
    }

    internal SchedulerSettings MoveSlot(int from, int to)
    {
        ValidateIndex(from);
        ValidateIndex(to);
        SchedulerEntry?[] copy = [.. Slots];
        SchedulerEntry? selected = copy[from];
        if (from < to)
        {
            Array.Copy(copy, from + 1, copy, from, to - from);
        }
        else if (from > to)
        {
            Array.Copy(copy, to, copy, to + 1, from - to);
        }

        copy[to] = selected;
        return Rebuild(copy);
    }

    internal SchedulerSettings Resize(int capacity)
    {
        if (capacity < Slots.Count && Slots.Skip(Math.Max(0, capacity)).Any(x => x is not null))
        {
            throw new InvalidDataException("登録済みの予約を失う容量変更はできません。");
        }

        return new SchedulerSettings(Slots.Take(Math.Max(0, capacity)),
            capacity, Enabled, Beep, WakeUp, OneShotStates);
    }

    private SchedulerSettings Rebuild(IEnumerable<SchedulerEntry?> slots)
    {
        SchedulerEntry?[] copied = [.. slots];
        return new SchedulerSettings(copied, Slots.Count, Enabled, Beep, WakeUp,
            OneShotStates.Where(state => state.AppliesTo(
                copied.FirstOrDefault(entry => entry?.Id == state.EntryId))));
    }

    private void ValidateIndex(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Slots.Count);
    }
}

internal static class SchedulerValidation
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            if (value.Contains('\0') || StrictUtf8.GetByteCount(value) > MaximumBytes)
            {
                throw new InvalidDataException("予約の文字列にNULまたは上限超過があります。");
            }
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("予約の文字列に不正なUTF-16があります。", error);
        }
    }
}
