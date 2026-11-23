namespace UWSCSharp.Wpf;

/// <summary>
/// 行ごとの受付済み窓を保持し、完全な観測から候補を選びます。
/// </summary>
internal sealed class SchedulerWindowMatcher
{
    private readonly Dictionary<Guid, Row> rows = [];
    private Guid generation;
    private Guid observationEpoch;
    private Row[] orderedRows = [];

    internal Guid Configure(Guid ownerGeneration, SchedulerSettings settings)
    {
        if (ownerGeneration == Guid.Empty)
        {
            throw new ArgumentException("監視世代は空にできません。", nameof(ownerGeneration));
        }
        ArgumentNullException.ThrowIfNull(settings);
        if (generation != ownerGeneration)
        {
            rows.Clear();
            generation = ownerGeneration;
        }

        var retained = new HashSet<Guid>();
        var configured = new List<Row>();
        if (settings.Enabled)
        {
            foreach (SchedulerEntry? entry in settings.Slots)
            {
                if (entry is not { Enabled: true, Condition: SchedulerWindowCondition condition })
                {
                    continue;
                }

                retained.Add(entry.Id);
                if (!rows.TryGetValue(entry.Id, out Row? row)
                    || row.Condition.Title != condition.Title
                    || row.Condition.ClassName != condition.ClassName)
                {
                    rows[entry.Id] = new(entry, condition);
                }
                else
                {
                    row.Entry = entry;
                }
                configured.Add(rows[entry.Id]);
            }
        }

        foreach (Guid id in rows.Keys.Where(id => !retained.Contains(id)).ToArray())
        {
            rows.Remove(id);
        }

        orderedRows = [.. configured];
        observationEpoch = Guid.NewGuid();
        return observationEpoch;
    }

    internal int Observe(Guid epoch, SchedulerWindowSnapshot snapshot,
        Func<SchedulerEntry, SchedulerWindowTrigger, bool> tryAccept)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(tryAccept);
        if (epoch == Guid.Empty || epoch != observationEpoch || snapshot.Error is not null)
        {
            return 0;
        }

        int accepted = 0;
        SchedulerObservedWindow[] ordered = [.. snapshot.Windows
            .OrderBy(window => window.Target.Handle)
            .ThenBy(window => window.Target.ProcessId)
            .ThenBy(window => window.Target.ThreadId)];
        foreach (Row row in orderedRows)
        {
            SchedulerWindowTarget[] matching = [.. ordered
                .Where(window => Matches(row.Condition, window))
                .Select(window => window.Target)];
            row.Accepted.IntersectWith(matching);
            foreach (SchedulerWindowTarget target in matching)
            {
                if (row.Accepted.Contains(target))
                {
                    continue;
                }

                if (tryAccept(row.Entry, new(target, snapshot.ObservedAt)))
                {
                    row.Accepted.Add(target);
                    accepted++;
                }
                break;
            }
        }

        return accepted;
    }

    private static bool Matches(SchedulerWindowCondition condition,
        SchedulerObservedWindow window) =>
        (condition.Title.Length == 0
            || window.Title.Contains(condition.Title, StringComparison.OrdinalIgnoreCase))
        && (condition.ClassName.Length == 0
            || window.ClassName.Equals(condition.ClassName, StringComparison.OrdinalIgnoreCase));

    private sealed class Row(SchedulerEntry entry, SchedulerWindowCondition condition)
    {
        internal SchedulerEntry Entry { get; set; } = entry;

        internal SchedulerWindowCondition Condition { get; } = condition;

        internal HashSet<SchedulerWindowTarget> Accepted { get; } = [];
    }
}
