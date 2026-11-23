using System.IO;

namespace UWSCSharp.Wpf;

internal readonly record struct SchedulerHistoryCursor(DateTimeOffset AcceptedAt, Guid OccurrenceId);

/// <summary>
/// 一覧には短い題名と状態だけを保持し、引数と診断の本文を複製しません。
/// </summary>
internal sealed record SchedulerHistorySummary(Guid OccurrenceId, string Title,
    DateTimeOffset AcceptedAt, SchedulerExecutionMode Mode,
    SchedulerExecutionOutcome? Outcome, bool Started, bool ResourcesRecovered,
    bool EndedConfirmed, bool TitleTruncated);

internal sealed record SchedulerHistoryPage(IReadOnlyList<SchedulerHistorySummary> Items,
    bool HasMore, SchedulerHistoryCursor? NextCursor);

internal sealed partial class SchedulerHistoryStore
{
    internal const int MaximumPageSize = 100;
    private const int MaximumSummaryTitleLength = 256;

    /// <summary>
    /// 受付UTCと発生識別子の降順で、指定位置より古い一覧を読みます。
    /// </summary>
    internal SchedulerHistoryPage ReadPage(SchedulerHistoryCursor? before = null,
        int pageSize = 50, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = new List<SchedulerHistorySummary>(pageSize + 1);
        if (!HasHistoryDirectory())
        {
            return new SchedulerHistoryPage([], false, null);
        }
        CheckDirectory();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(path), "N", out Guid id))
            {
                continue;
            }
            SchedulerHistoryRecord record;
            try
            {
                record = ReadNamedRecord(path, id);
            }
            catch (FileNotFoundException)
            {
                // 終了済み履歴の整理で列挙後に消えたファイルだけを読み飛ばします。
                continue;
            }
            var cursor = new SchedulerHistoryCursor(record.AcceptedAt, record.OccurrenceId);
            if (before is { } boundary && Compare(cursor, boundary) >= 0)
            {
                continue;
            }
            selected.Add(new SchedulerHistorySummary(record.OccurrenceId, SummaryTitle(record.Title),
                record.AcceptedAt, record.Mode, record.Outcome, record.Started,
                record.ResourcesRecovered, record.EndedAt is not null,
                record.Title.Length > MaximumSummaryTitleLength));
            selected.Sort((left, right) => Compare(
                new SchedulerHistoryCursor(right.AcceptedAt, right.OccurrenceId),
                new SchedulerHistoryCursor(left.AcceptedAt, left.OccurrenceId)));
            if (selected.Count > pageSize + 1)
            {
                selected.RemoveAt(selected.Count - 1);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        bool hasMore = selected.Count > pageSize;
        SchedulerHistorySummary[] items = [.. selected.Take(pageSize)];
        SchedulerHistoryCursor? next = hasMore
            ? new(items[^1].AcceptedAt, items[^1].OccurrenceId) : null;
        return new SchedulerHistoryPage(Array.AsReadOnly(items), hasMore, next);
    }

    /// <summary>
    /// 選択した発生の完全な記録だけを読みます。消失は失敗として通知します。
    /// </summary>
    internal SchedulerHistoryRecord ReadRecord(Guid occurrenceId)
    {
        if (occurrenceId == Guid.Empty)
        {
            throw new ArgumentException("履歴の発生識別子が空です。", nameof(occurrenceId));
        }
        CheckDirectory();
        return ReadNamedRecord(System.IO.Path.Combine(directory,
            occurrenceId.ToString("N") + ".json"), occurrenceId);
    }

    private static SchedulerHistoryRecord ReadNamedRecord(string path, Guid id)
    {
        SchedulerHistoryRecord record = Read(path);
        if (record.OccurrenceId != id
            || System.IO.Path.GetFileNameWithoutExtension(path) != id.ToString("N"))
        {
            throw new InvalidDataException("履歴のファイル名と発生識別子が一致しません。");
        }
        return record;
    }

    private bool HasHistoryDirectory()
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(directory);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("履歴の保存先がフォルダーではありません。");
        }
        return true;
    }

    private static int Compare(SchedulerHistoryCursor left, SchedulerHistoryCursor right)
    {
        int time = left.AcceptedAt.CompareTo(right.AcceptedAt);
        return time != 0 ? time : left.OccurrenceId.CompareTo(right.OccurrenceId);
    }

    private static string SummaryTitle(string title)
    {
        if (title.Length <= MaximumSummaryTitleLength)
        {
            return title;
        }
        int length = MaximumSummaryTitleLength - 1;
        if (char.IsHighSurrogate(title[length - 1]))
        {
            length--;
        }
        return title[..length] + "…";
    }
}
