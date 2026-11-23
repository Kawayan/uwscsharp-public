namespace UWSCSharp.Wpf;

/// <summary>
/// 親の観測を保存待ちへ積み、保存失敗を実行結果から分離します。
/// </summary>
internal sealed class SchedulerHistoryJournal(
    Func<SchedulerHistoryRecord, CancellationToken, Task> save)
{
    private readonly object state = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly Dictionary<Guid, SchedulerHistoryRecord> pending = [];
    private Exception? lastError;

    internal Exception? LastError => Volatile.Read(ref lastError);

    internal bool IsWriting => writer.CurrentCount == 0;

    internal bool HasPending
    {
        get
        {
            lock (state)
            {
                return pending.Count > 0;
            }
        }
    }

    internal void RecordFailure(Exception error) => Volatile.Write(ref lastError, error);

    internal void Enqueue(SchedulerHistoryRecord record)
    {
        lock (state)
        {
            if (!pending.TryGetValue(record.OccurrenceId, out SchedulerHistoryRecord? previous)
                || previous.Revision < record.Revision)
            {
                pending[record.OccurrenceId] = record;
            }
            SchedulerHistoryRecord[] obsolete = [.. pending.Values
                .Where(value => !value.IsProtected)
                .OrderByDescending(value => value.EndedAt)
                .ThenByDescending(value => value.OccurrenceId)
                .Skip(SchedulerHistoryStore.MaximumCompletedRecords)];
            foreach (SchedulerHistoryRecord value in obsolete)
            {
                pending.Remove(value.OccurrenceId);
            }
            if (obsolete.Length > 0)
            {
                Volatile.Write(ref lastError, new InvalidOperationException(
                    "保存待ち履歴の上限により、古い回収済み履歴の保存を省略しました。"));
            }
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool progressed;
            do
            {
                progressed = false;
                SchedulerHistoryRecord[] snapshot;
                lock (state)
                {
                    snapshot = [.. pending.Values];
                }
                foreach (SchedulerHistoryRecord record in snapshot)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await save(record, cancellationToken).ConfigureAwait(false);
                        progressed = true;
                        lock (state)
                        {
                            if (pending.TryGetValue(record.OccurrenceId, out SchedulerHistoryRecord? current)
                                && current.Revision == record.Revision)
                            {
                                pending.Remove(record.OccurrenceId);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        Volatile.Write(ref lastError, error);
                    }
                }
            }
            while (progressed);
        }
        finally
        {
            writer.Release();
        }
    }
}
