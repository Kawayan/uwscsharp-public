using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 一時履歴と保存の代替だけを使い、画面や入力を操作しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerHistoryBrowsingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "uwsc-history-browse-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Accepted = new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

    private static SchedulerHistoryRecord Record(int index, string? title = null)
    {
        var entry = new SchedulerEntry(Guid.NewGuid(), title ?? "保存した題名" + index,
            Path.Combine(Path.GetTempPath(), "history.uws"), ["", "a b", "日本語\n次行"],
            SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), Path.GetTempPath());
        return SchedulerHistoryRecord.Capture(new SchedulerDispatch(Guid.NewGuid(),
            new SchedulerExecutionRequest(Guid.ParseExact(index.ToString("x32"), "N"), entry, Accepted)));
    }

    private SchedulerHistoryStore Store() => new(directory);

    private static SchedulerHistoryPage Page(params SchedulerHistoryRecord[] records) =>
        new(Array.AsReadOnly(records.Select(value => new SchedulerHistorySummary(value.OccurrenceId,
            value.Title, value.AcceptedAt, value.Mode, value.Outcome, value.Started,
            value.ResourcesRecovered, value.EndedAt is not null, false)).ToArray()), false, null);

    [Fact]
    public void PagingUsesStableIdForTiedAcceptedTimesAndDoesNotModifyFiles()
    {
        SchedulerHistoryStore store = Store();
        foreach (int index in Enumerable.Range(1, 5))
        {
            store.Save(Record(index));
        }
        Dictionary<string, byte[]> before = Directory.GetFiles(directory, "*.json")
            .ToDictionary(path => path, File.ReadAllBytes);
        SchedulerHistoryPage first = store.ReadPage(pageSize: 2);
        Assert.Equal([Record(5).OccurrenceId, Record(4).OccurrenceId], first.Items.Select(value => value.OccurrenceId));
        SchedulerHistoryPage second = store.ReadPage(first.NextCursor, 2);
        Assert.Equal([Record(3).OccurrenceId, Record(2).OccurrenceId], second.Items.Select(value => value.OccurrenceId));
        SchedulerHistoryPage last = store.ReadPage(second.NextCursor, 2);
        Assert.Equal(Record(1).OccurrenceId, Assert.Single(last.Items).OccurrenceId);
        Assert.False(last.HasMore);
        Assert.Null(last.NextCursor);
        foreach (KeyValuePair<string, byte[]> file in before)
        {
            Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        }
    }

    [Fact]
    public void ClockRollbackRemainsSortedByStoredUtcWithoutInventedEndOrRecovery()
    {
        SchedulerHistoryStore store = Store();
        SchedulerHistoryRecord later = Record(1);
        SchedulerHistoryRecord rollback = Record(2) with
        {
            AcceptedAt = Accepted.AddHours(-1)
        };
        store.Save(later);
        store.Save(rollback);
        SchedulerHistoryPage page = store.ReadPage();
        Assert.Equal(later.OccurrenceId, page.Items[0].OccurrenceId);
        Assert.All(page.Items, item => { Assert.False(item.ResourcesRecovered); Assert.False(item.EndedConfirmed); });
        Assert.Equal(Accepted.AddHours(-1), store.ReadRecord(rollback.OccurrenceId).AcceptedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void InvalidPageSizeIsRejected(int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Store().ReadPage(pageSize: size));

    [Fact]
    public void MissingDirectoryIsEmptyButFileInDirectoryPositionIsFailure()
    {
        Assert.Empty(Store().ReadPage().Items);
        Assert.False(Directory.Exists(directory));
        File.WriteAllText(directory, "keep");
        try
        {
            Assert.Throws<IOException>(() => Store().ReadPage());
            Assert.Equal("keep", File.ReadAllText(directory));
        }
        finally
        {
            File.Delete(directory);
        }
    }

    [Fact]
    public void SummaryIsBoundedWithoutSplittingSurrogateAndDetailKeepsFullSnapshot()
    {
        string title = new string('a', 254) + "😀" + new string('b', 30);
        SchedulerHistoryRecord original = Record(1, title);
        Store().Save(original);
        SchedulerHistorySummary item = Assert.Single(Store().ReadPage().Items);
        Assert.True(item.TitleTruncated);
        Assert.True(item.Title.Length <= 256);
        Assert.False(char.IsSurrogate(item.Title[^2]));
        SchedulerHistoryRecord detail = Store().ReadRecord(original.OccurrenceId);
        Assert.Equal(title, detail.Title);
        Assert.Equal(original.Arguments, detail.Arguments);
        Assert.Equal(original.Path, detail.Path);
    }

    [Fact]
    public void CorruptRecordAndMismatchedFileIdentityAreNotPartialSuccess()
    {
        SchedulerHistoryStore store = Store();
        SchedulerHistoryRecord record = Record(1);
        store.Save(record);
        string path = Path.Combine(directory, record.OccurrenceId.ToString("N") + ".json");
        string corrupt = Path.Combine(directory, Record(2).OccurrenceId.ToString("N") + ".json");
        File.Move(path, corrupt);
        Assert.Throws<InvalidDataException>(() => store.ReadPage());
        File.Move(corrupt, path);
        File.WriteAllText(corrupt, "{");
        Assert.ThrowsAny<Exception>(() => store.ReadPage());
        Assert.Equal("{", File.ReadAllText(corrupt));
    }

    [Fact]
    public void CancelledScanDoesNotReplaceOrDeleteHistory()
    {
        SchedulerHistoryRecord record = Record(1);
        Store().Save(record);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Store().ReadPage(cancellationToken: cancelled.Token));
        Assert.Equal(record.OccurrenceId, Assert.Single(Store().Load()).OccurrenceId);
    }

    [Fact]
    public async Task FailedRefreshKeepsPreviousPageAndSelectedDetail()
    {
        SchedulerHistoryRecord record = Record(1);
        bool fail = false;
        var browser = new SchedulerHistoryBrowser((_, _) => fail
            ? Task.FromException<SchedulerHistoryPage>(new IOException("読込み拒否"))
            : Task.FromResult(Page(record)), (_, _) => Task.FromResult(record));
        Assert.True(await browser.LoadPageAsync(null));
        Assert.True(await browser.SelectAsync(record.OccurrenceId));
        SchedulerHistoryPage previous = browser.Current!;
        fail = true;
        Assert.False(await browser.LoadPageAsync(null));
        Assert.Same(previous, browser.Current);
        Assert.Same(record, browser.Detail);
        Assert.IsType<IOException>(browser.PageError);
    }

    [Fact]
    public async Task LatePageCannotOverwriteNewerSuccessfulRead()
    {
        var delayed = new TaskCompletionSource<SchedulerHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        SchedulerHistoryRecord latest = Record(2);
        var browser = new SchedulerHistoryBrowser((_, _) => ++calls == 1
            ? delayed.Task : Task.FromResult(Page(latest)), (_, _) => Task.FromResult(latest));
        Task<bool> old = browser.LoadPageAsync(null);
        Assert.True(await browser.LoadPageAsync(null));
        delayed.SetResult(Page(Record(1)));
        Assert.False(await old);
        Assert.Equal(latest.OccurrenceId, Assert.Single(browser.Current!.Items).OccurrenceId);
    }

    [Fact]
    public async Task LateDetailCannotAppearUnderNewSelection()
    {
        SchedulerHistoryRecord first = Record(1);
        SchedulerHistoryRecord second = Record(2);
        var delayed = new TaskCompletionSource<SchedulerHistoryRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new SchedulerHistoryBrowser((_, _) => Task.FromResult(Page(first, second)),
            (id, _) => id == first.OccurrenceId ? delayed.Task : Task.FromResult(second));
        await browser.LoadPageAsync(null);
        Task<bool> old = browser.SelectAsync(first.OccurrenceId);
        Assert.True(await browser.SelectAsync(second.OccurrenceId));
        delayed.SetResult(first);
        Assert.False(await old);
        Assert.Same(second, browser.Detail);
    }

    [Fact]
    public async Task DetailFailureClearsOldDefinitionAndMissingSelectionIsIgnored()
    {
        SchedulerHistoryRecord record = Record(1);
        bool fail = false;
        var browser = new SchedulerHistoryBrowser((_, _) => Task.FromResult(Page(record)),
            (_, _) => fail ? Task.FromException<SchedulerHistoryRecord>(new FileNotFoundException()) : Task.FromResult(record));
        await browser.LoadPageAsync(null);
        await browser.SelectAsync(record.OccurrenceId);
        fail = true;
        Assert.False(await browser.SelectAsync(record.OccurrenceId));
        Assert.Null(browser.Detail);
        Assert.IsType<FileNotFoundException>(browser.DetailError);
        Assert.False(await browser.SelectAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task CancellationDiscardsResponseEvenIfReaderDoesNotObserveToken()
    {
        var delayed = new TaskCompletionSource<SchedulerHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new SchedulerHistoryBrowser((_, _) => delayed.Task, (_, _) => Task.FromResult(Record(1)));
        using var cancelled = new CancellationTokenSource();
        Task<bool> reading = browser.LoadPageAsync(null, cancelled.Token);
        cancelled.Cancel();
        delayed.SetResult(Page(Record(1)));
        Assert.False(await reading);
        Assert.Null(browser.Current);
        Assert.Null(browser.PageError);
    }

    [Fact]
    public void DetailsSeparateResultObservationFromUnconfirmedEndAndKeepArguments()
    {
        SchedulerHistoryRecord record = Record(1).MarkStarted(Accepted).Complete(
            SchedulerExecutionResult.Unknown(true, null, false, "保存した診断"),
            Accepted.AddSeconds(1), endedConfirmed: false);
        string text = SchedulerHistoryPresentation.Details(record);
        Assert.Contains("結果受取: 2027-01-02 03:04:01.000 UTC", text);
        Assert.Contains("終了確認: 未確認", text);
        Assert.Contains("資源回収: 未確認", text);
        Assert.Contains("結果: 結果不明", text);
        Assert.Contains("1: \"\"", text);
        Assert.Contains("3: \"日本語\\n次行\"", text);
        Assert.Contains(record.Path, text);
        Assert.Contains("保存した診断", text);
    }

    public void Dispose()
    {
        string target = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(target) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(target).StartsWith("uwsc-history-browse-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("検査用フォルダーの範囲が一致しません。");
        }
        if (Directory.Exists(target))
        {
            Directory.Delete(target, true);
        }
    }
}
