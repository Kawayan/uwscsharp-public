namespace UWSCSharp.Wpf;

/// <summary>
/// 非同期読込みの古い応答を無視し、失敗時は最後の正常一覧を保持します。
/// </summary>
internal sealed class SchedulerHistoryBrowser(
    Func<SchedulerHistoryCursor?, CancellationToken, Task<SchedulerHistoryPage>> readPage,
    Func<Guid, CancellationToken, Task<SchedulerHistoryRecord>> readRecord)
{
    private readonly object state = new();
    private long pageRequest;
    private long detailRequest;
    private SchedulerHistoryPage? current;
    private SchedulerHistoryRecord? detail;
    private Exception? pageError;
    private Exception? detailError;

    internal SchedulerHistoryPage? Current
    {
        get
        {
            lock (state)
            {
                return current;
            }
        }
    }

    internal SchedulerHistoryRecord? Detail
    {
        get
        {
            lock (state)
            {
                return detail;
            }
        }
    }

    internal Exception? PageError
    {
        get
        {
            lock (state)
            {
                return pageError;
            }
        }
    }

    internal Exception? DetailError
    {
        get
        {
            lock (state)
            {
                return detailError;
            }
        }
    }

    internal async Task<bool> LoadPageAsync(SchedulerHistoryCursor? before,
        CancellationToken cancellationToken = default)
    {
        long request;
        lock (state)
        {
            request = ++pageRequest;
        }
        try
        {
            SchedulerHistoryPage page = await readPage(before, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (state)
            {
                if (request != pageRequest)
                {
                    return false;
                }
                current = page;
                pageError = null;
                detailError = null;
                detail = null;
                detailRequest++;
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            lock (state)
            {
                if (request == pageRequest)
                {
                    pageError = error;
                }
            }
            return false;
        }
    }

    internal async Task<bool> SelectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        long request;
        long page;
        lock (state)
        {
            if (current is null || !current.Items.Any(value => value.OccurrenceId == id))
            {
                return false;
            }
            request = ++detailRequest;
            page = pageRequest;
            detail = null;
            detailError = null;
        }
        try
        {
            SchedulerHistoryRecord record = await readRecord(id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (record.OccurrenceId != id)
            {
                throw new System.IO.InvalidDataException("選択した履歴と読込み結果の識別子が異なります。");
            }
            lock (state)
            {
                if (request != detailRequest || page != pageRequest)
                {
                    return false;
                }
                detail = record;
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            lock (state)
            {
                if (request == detailRequest && page == pageRequest)
                {
                    detailError = error;
                }
            }
            return false;
        }
    }
}
