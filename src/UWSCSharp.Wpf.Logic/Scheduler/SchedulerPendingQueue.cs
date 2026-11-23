namespace UWSCSharp.Wpf;

internal sealed record SchedulerExecutionRequest(Guid OccurrenceId,
    SchedulerEntry Entry, DateTimeOffset ObservedAt,
    SchedulerWindowTrigger? WindowTrigger = null);

/// <summary>
/// 監視所有者が順に操作する待機と引渡し済み要求の台帳です。起動は行いません。
/// </summary>
internal sealed class SchedulerPendingQueue
{
    private readonly LinkedList<SchedulerExecutionRequest> pending = new();
    private readonly Dictionary<Guid, SchedulerExecutionRequest> running = [];

    internal int PendingCount => pending.Count;

    internal int RunningCount => running.Count;

    internal bool CanAccept(Guid entryId) => pending.Count < SchedulerSettings.MaximumCapacity
        && !pending.Any(item => item.Entry.Id == entryId);

    internal bool ContainsOccurrence(Guid occurrenceId) =>
        pending.Any(item => item.OccurrenceId == occurrenceId)
        || running.Values.Any(item => item.OccurrenceId == occurrenceId);

    internal SchedulerExecutionRequest? FindRunning(Guid occurrenceId) =>
        running.Values.FirstOrDefault(item => item.OccurrenceId == occurrenceId);

    internal bool TryEnqueue(SchedulerExecutionRequest request)
    {
        if (request.OccurrenceId == Guid.Empty)
        {
            throw new ArgumentException("発生識別子は空にできません。", nameof(request));
        }

        if ((request.Entry.Condition is SchedulerWindowCondition) != (request.WindowTrigger is not null)
            || request.WindowTrigger is { } trigger
                && (trigger.Target.Handle == 0 || trigger.Target.ProcessId == 0
                    || trigger.Target.ThreadId == 0 || trigger.ObservedAt != request.ObservedAt))
        {
            throw new ArgumentException("窓条件と捕捉先または観測日時が一致しません。", nameof(request));
        }

        if (!request.Entry.Enabled || pending.Count >= SchedulerSettings.MaximumCapacity
            || pending.Any(item => item.Entry.Id == request.Entry.Id)
            || pending.Any(item => item.OccurrenceId == request.OccurrenceId)
            || running.Values.Any(item => item.OccurrenceId == request.OccurrenceId))
        {
            return false;
        }

        pending.AddLast(request);
        return true;
    }

    internal SchedulerExecutionRequest? TryTake(bool externalSameProcessBusy = false,
        bool allowSeparateProcess = true)
    {
        if (running.Count >= SchedulerSettings.MaximumCapacity)
        {
            return null;
        }

        bool sameBusy = externalSameProcessBusy || running.Values.Any(
            item => item.Entry.Mode == SchedulerExecutionMode.SameProcess);
        for (LinkedListNode<SchedulerExecutionRequest>? node = pending.First;
            node is not null; node = node.Next)
        {
            SchedulerExecutionRequest request = node.Value;
            bool eligible = !running.ContainsKey(request.Entry.Id)
                && (request.Entry.Mode == SchedulerExecutionMode.SameProcess
                    ? !sameBusy : allowSeparateProcess);
            if (eligible)
            {
                pending.Remove(node);
                running.Add(request.Entry.Id, request);
                return request;
            }
        }

        return null;
    }

    internal bool Complete(Guid occurrenceId)
    {
        foreach (KeyValuePair<Guid, SchedulerExecutionRequest> pair in running)
        {
            if (pair.Value.OccurrenceId == occurrenceId)
            {
                return running.Remove(pair.Key);
            }
        }

        return false;
    }

    internal IReadOnlyList<SchedulerExecutionRequest> Reconcile(SchedulerSettings settings)
    {
        var current = new Dictionary<Guid, SchedulerEntry>();
        if (settings.Enabled)
        {
            foreach (SchedulerEntry? entry in settings.Slots)
            {
                if (entry is not null && entry.Enabled)
                {
                    current.Add(entry.Id, entry);
                }
            }
        }

        var removed = new List<SchedulerExecutionRequest>();
        LinkedListNode<SchedulerExecutionRequest>? node = pending.First;
        while (node is not null)
        {
            LinkedListNode<SchedulerExecutionRequest>? next = node.Next;
            SchedulerExecutionRequest request = node.Value;
            if (!current.TryGetValue(request.Entry.Id, out SchedulerEntry? replacement)
                || !SchedulerExecutionDefinition.Same(request.Entry, replacement))
            {
                removed.Add(request);
                pending.Remove(node);
            }
            else
            {
                // 表示名だけの変更で古い大きな表示文字列を保持し続けない。
                node.Value = request with
                {
                    Entry = replacement
                };
            }

            node = next;
        }

        return removed.AsReadOnly();
    }

    internal IReadOnlyList<SchedulerExecutionRequest> CancelAll()
    {
        SchedulerExecutionRequest[] cancelled = [.. pending];
        pending.Clear();
        return Array.AsReadOnly(cancelled);
    }
}
