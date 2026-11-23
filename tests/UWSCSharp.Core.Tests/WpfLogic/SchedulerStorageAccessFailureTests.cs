using System.Runtime.Versioning;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 専用一時ファイルへの実アクセス拒否を使い、画面や入力を操作しません。
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerStorageAccessFailureTests : IDisposable
{
    private static readonly DateTimeOffset Accepted = new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-storage-access-" + Guid.NewGuid().ToString("N"));

    public SchedulerStorageAccessFailureTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ReadOnlyHistoryPreservesPreviousBytesAndPendingResultUntilRetry(int exitCode)
    {
        var storage = new SchedulerManagedStorage(directory);
        var journal = new SchedulerHistoryJournal(storage.SaveHistoryAsync);
        SchedulerHistoryRecord accepted = Capture();
        await storage.SaveHistoryAsync(accepted);
        string path = HistoryPath(accepted.OccurrenceId);
        byte[] previous = await File.ReadAllBytesAsync(path);
        SchedulerHistoryRecord complete = accepted.MarkStarted(Accepted.AddSeconds(1))
            .Complete(SchedulerExecutionResult.Completed(exitCode, true, false, true),
                Accepted.AddSeconds(2), endedConfirmed: true);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            journal.Enqueue(complete);
            await journal.FlushAsync();
            UnauthorizedAccessException error = Assert.IsType<UnauthorizedAccessException>(journal.LastError);
            Assert.Equal(5, error.HResult & 0xffff);
            Assert.True(journal.HasPending);
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.Equal(0, storage.ReadHistoryRecord(accepted.OccurrenceId).Revision);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, ".history-*.tmp"));
        }
        finally
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        await journal.FlushAsync();
        Assert.False(journal.HasPending);
        SchedulerHistoryRecord saved = storage.ReadHistoryRecord(accepted.OccurrenceId);
        Assert.Equal(complete.Revision, saved.Revision);
        Assert.Equal(exitCode, saved.ExitCode);
        Assert.Equal(exitCode == 0 ? SchedulerExecutionOutcome.Succeeded : SchedulerExecutionOutcome.Failed,
            saved.Outcome);
        Assert.True(saved.Started && saved.ResourcesRecovered && !saved.IsProtected);
        Assert.Equal(complete.Arguments, saved.Arguments);
        Assert.Equal(complete.StartedAt, saved.StartedAt);
        Assert.Equal(complete.EndedAt, saved.EndedAt);
    }

    [Fact]
    public async Task ReadOnlyPruneFailurePreservesProtectedLogsAndReleasesStorageLock()
    {
        var storage = new SchedulerManagedStorage(directory, 10);
        SchedulerHistoryRecord completed = Capture().Complete(
            SchedulerExecutionResult.Completed(0, true, false, true),
            Accepted.AddSeconds(1), endedConfirmed: true);
        SchedulerHistoryRecord running = Capture();
        await storage.SaveHistoryAsync(completed);
        await storage.SaveHistoryAsync(running);
        await WriteLog(storage, completed.OccurrenceId, [1, 2, 3, 4, 5, 6]);
        await WriteLog(storage, running.OccurrenceId, [7, 8, 9, 10]);
        string path = storage.LogPath(completed.OccurrenceId);
        var next = Guid.NewGuid();
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            UnauthorizedAccessException error = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            {
                using IDisposable lease = await storage.AcquireDefaultSaveAsync(storage.LogPath(next), 4, default);
            });
            Assert.Equal(5, error.HResult & 0xffff);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(new byte[] { 7, 8, 9, 10 }, await File.ReadAllBytesAsync(storage.LogPath(running.OccurrenceId)));
            Assert.False(File.Exists(storage.LogPath(next)));
            Assert.Equal(2, storage.LoadHistory().Count);
        }
        finally
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
        await WriteLog(storage, next, [11, 12, 13, 14]);
        Assert.False(File.Exists(path));
        Assert.Equal(new byte[] { 7, 8, 9, 10 }, await File.ReadAllBytesAsync(storage.LogPath(running.OccurrenceId)));
        Assert.Equal(new byte[] { 11, 12, 13, 14 }, await File.ReadAllBytesAsync(storage.LogPath(next)));
        Assert.True(storage.ReadHistoryRecord(running.OccurrenceId).IsProtected);
        Assert.Equal(2, storage.LoadHistory().Count);
    }

    private SchedulerHistoryRecord Capture()
    {
        var entry = new SchedulerEntry(Guid.NewGuid(), "保存拒否の確認",
            Path.Combine(directory, "main.uws"), ["", "a b", "日本語"],
            SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), directory);
        return SchedulerHistoryRecord.Capture(new SchedulerDispatch(Guid.NewGuid(),
            new SchedulerExecutionRequest(Guid.NewGuid(), entry, Accepted)));
    }

    private string HistoryPath(Guid id) => Path.Combine(directory, "managed", "history", id.ToString("N") + ".json");

    private static async Task WriteLog(SchedulerManagedStorage storage, Guid id, byte[] content)
    {
        using IDisposable lease = await storage.AcquireDefaultSaveAsync(storage.LogPath(id), content.Length, default);
        await File.WriteAllBytesAsync(storage.LogPath(id), content);
    }
}
