using System.Runtime.Versioning;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 実ファイルの排他と保存待ちを使い、画面や入力を操作しません。
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerHistorySaveContentionTests : IDisposable
{
    private static readonly DateTimeOffset Accepted = new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-history-contention-" + Guid.NewGuid().ToString("N"));

    public SchedulerHistorySaveContentionTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineCancelsOnlyWaitingFlushAndRetainsLatestRevision(bool anotherWriter)
    {
        var storage = new SchedulerManagedStorage(directory);
        var journal = new SchedulerHistoryJournal(storage.SaveHistoryAsync);
        SchedulerHistoryRecord accepted = Capture();
        await storage.SaveHistoryAsync(accepted);
        string path = HistoryPath(accepted.OccurrenceId);
        byte[] previous = await File.ReadAllBytesAsync(path);
        SchedulerHistoryRecord started = accepted.MarkStarted(Accepted.AddSeconds(1));
        SchedulerHistoryRecord complete = started.Complete(
            SchedulerExecutionResult.Completed(7, true, false, true),
            Accepted.AddSeconds(2), endedConfirmed: true);
        IDisposable ownership = await storage.AcquireDefaultSaveAsync(
            storage.LogPath(Guid.NewGuid()), 0, default);
        Task? activeWriter = null;
        try
        {
            if (anotherWriter)
            {
                journal.Enqueue(started);
                activeWriter = journal.FlushAsync();
                Assert.False(activeWriter.IsCompleted);
            }
            journal.Enqueue(complete);
            using var deadline = new CancellationTokenSource();
            Task waiting = journal.FlushAsync(deadline.Token);
            Assert.False(waiting.IsCompleted);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(250));
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(deadline.IsCancellationRequested);
            Assert.Equal(deadline.Token, error.CancellationToken);
            Assert.True(journal.HasPending);
            Assert.Null(journal.LastError);
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, ".history-*.tmp"));
            if (activeWriter is not null)
            {
                Assert.False(activeWriter.IsCompleted);
            }
        }
        finally
        {
            ownership.Dispose();
            if (activeWriter is not null)
            {
                await activeWriter.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        await journal.FlushAsync();
        AssertSavedLatest(storage, journal, complete);
    }

    [Fact]
    public async Task ActualHistoryWriterConflictRetainsPreviousBytesAndCanRetry()
    {
        var storage = new SchedulerManagedStorage(directory);
        var journal = new SchedulerHistoryJournal(storage.SaveHistoryAsync);
        SchedulerHistoryRecord accepted = Capture();
        await storage.SaveHistoryAsync(accepted);
        string path = HistoryPath(accepted.OccurrenceId);
        byte[] previous = await File.ReadAllBytesAsync(path);
        SchedulerHistoryRecord complete = accepted.MarkStarted(Accepted.AddSeconds(1))
            .Complete(SchedulerExecutionResult.Completed(7, true, false, true),
                Accepted.AddSeconds(2), endedConfirmed: true);
        using (var owner = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, ".history.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            journal.Enqueue(complete);
            await journal.FlushAsync();
            IOException error = Assert.IsType<IOException>(journal.LastError);
            Assert.Equal(32, error.HResult & 0xffff);
            Assert.True(journal.HasPending);
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, ".history-*.tmp"));
        }
        await journal.FlushAsync();
        AssertSavedLatest(storage, journal, complete);
    }

    private static void AssertSavedLatest(SchedulerManagedStorage storage,
        SchedulerHistoryJournal journal, SchedulerHistoryRecord complete)
    {
        Assert.False(journal.HasPending);
        SchedulerHistoryRecord saved = storage.ReadHistoryRecord(complete.OccurrenceId);
        Assert.Equal(2, saved.Revision);
        Assert.Equal(complete.Arguments, saved.Arguments);
        Assert.Equal(complete.StartedAt, saved.StartedAt);
        Assert.Equal(complete.EndedAt, saved.EndedAt);
        Assert.Equal(7, saved.ExitCode);
        Assert.Equal(SchedulerExecutionOutcome.Failed, saved.Outcome);
        Assert.True(saved.Started && saved.ResourcesRecovered && !saved.IsProtected);
    }

    private SchedulerHistoryRecord Capture()
    {
        var entry = new SchedulerEntry(Guid.NewGuid(), "保存待ちの競合",
            Path.Combine(directory, "main.uws"), ["", "a b", "日本語"],
            SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), directory);
        return SchedulerHistoryRecord.Capture(new SchedulerDispatch(Guid.NewGuid(),
            new SchedulerExecutionRequest(Guid.NewGuid(), entry, Accepted)));
    }

    private string HistoryPath(Guid id) => Path.Combine(directory, "managed", "history", id.ToString("N") + ".json");
}
