using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 一時ファイルとWin32の保存先照合だけを使い、画面や入力を操作しません。
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerManagedStorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-managed-logs-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Accepted = new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

    public SchedulerManagedStorageTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, true);

    private SchedulerHistoryRecord Capture(int seconds = 0, bool completed = false)
    {
        var entry = new SchedulerEntry(Guid.NewGuid(), "管理ログ",
            Path.Combine(directory, "main.uws"), [],
            SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), directory);
        var record = SchedulerHistoryRecord.Capture(new SchedulerDispatch(
            Guid.NewGuid(), new SchedulerExecutionRequest(Guid.NewGuid(), entry, Accepted.AddSeconds(seconds))));
        return completed ? record.Complete(SchedulerExecutionResult.Completed(0, true, false, true),
            Accepted.AddSeconds(seconds + 1), endedConfirmed: true) : record;
    }

    private static async Task WriteLog(SchedulerManagedStorage storage, Guid id, int bytes)
    {
        using IDisposable lease = await storage.AcquireDefaultSaveAsync(storage.LogPath(id), bytes, default);
        await File.WriteAllBytesAsync(storage.LogPath(id), new byte[bytes]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task InitialSaveAcceptsExactCapacityAndEmptyFile(int bytes)
    {
        var storage = new SchedulerManagedStorage(directory, 10);
        var id = Guid.NewGuid();
        await WriteLog(storage, id, bytes);
        Assert.Equal(bytes, new FileInfo(storage.LogPath(id)).Length);
    }

    [Fact]
    public async Task OversizedSingleRequestDoesNotDeleteCompletedLogs()
    {
        var storage = new SchedulerManagedStorage(directory, 10);
        SchedulerHistoryRecord completed = Capture(completed: true);
        await storage.SaveHistoryAsync(completed);
        await WriteLog(storage, completed.OccurrenceId, 3);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                using IDisposable lease = await storage.AcquireDefaultSaveAsync(storage.LogPath(Guid.NewGuid()), 11, default);
            });
        Assert.Equal(3, new FileInfo(storage.LogPath(completed.OccurrenceId)).Length);
    }

    [Fact]
    public async Task ExistingOverCapacityIsPreservedWithoutDeletingCompletedFiles()
    {
        var storage = new SchedulerManagedStorage(directory, 10);
        SchedulerHistoryRecord completed = Capture(completed: true);
        await storage.SaveHistoryAsync(completed);
        await WriteLog(storage, completed.OccurrenceId, 3);
        string legacy = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log");
        await File.WriteAllBytesAsync(legacy, new byte[11]);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                using IDisposable lease = await storage.AcquireDefaultSaveAsync(storage.LogPath(Guid.NewGuid()), 1, default);
            });
        Assert.True(File.Exists(storage.LogPath(completed.OccurrenceId)));
        Assert.Equal(11, new FileInfo(legacy).Length);
    }

    [Fact]
    public async Task CapacityPrunesOnlyOldCompletedManagedLogs()
    {
        var storage = new SchedulerManagedStorage(directory, 10);
        SchedulerHistoryRecord completed = Capture(completed: true);
        SchedulerHistoryRecord running = Capture(1);
        await storage.SaveHistoryAsync(completed);
        await storage.SaveHistoryAsync(running);
        await WriteLog(storage, completed.OccurrenceId, 3);
        await WriteLog(storage, running.OccurrenceId, 3);
        string legacy = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".log");
        await File.WriteAllBytesAsync(legacy, new byte[3]);
        var next = Guid.NewGuid();
        await WriteLog(storage, next, 4);
        Assert.False(File.Exists(storage.LogPath(completed.OccurrenceId)));
        Assert.True(File.Exists(storage.LogPath(running.OccurrenceId)));
        Assert.Equal(3, new FileInfo(legacy).Length);
        Assert.Equal(4, new FileInfo(storage.LogPath(next)).Length);
        Assert.Equal(2, storage.LoadHistory().Count);
    }

    [Fact]
    public async Task UnknownRecoveryAndUnclassifiedFilesAreProtected()
    {
        var storage = new SchedulerManagedStorage(directory, 10);
        SchedulerHistoryRecord unrecovered = Capture().Complete(
            SchedulerExecutionResult.Unknown(true, null, false, "回収未確認"),
            Accepted.AddSeconds(1), endedConfirmed: false);
        await storage.SaveHistoryAsync(unrecovered);
        await WriteLog(storage, unrecovered.OccurrenceId, 6);
        var unclassified = Guid.NewGuid();
        await WriteLog(storage, unclassified, 4);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                using IDisposable lease = await storage.AcquireDefaultSaveAsync(storage.LogPath(Guid.NewGuid()), 1, default);
            });
        Assert.Equal(6, new FileInfo(storage.LogPath(unrecovered.OccurrenceId)).Length);
        Assert.True(File.Exists(storage.LogPath(unclassified)));
        Assert.True(Assert.Single(storage.LoadHistory()).IsProtected);
    }

    [Fact]
    public async Task ReplacementReservesTemporaryFileCapacityAndPreservesCurrentLog()
    {
        var storage = new SchedulerManagedStorage(directory, 10);
        var id = Guid.NewGuid();
        await WriteLog(storage, id, 6);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                using IDisposable lease = await storage.AcquireDefaultSaveAsync(storage.LogPath(id), 5, default);
            });
        Assert.Equal(6, new FileInfo(storage.LogPath(id)).Length);
        await WriteLog(storage, id, 4);
        Assert.Equal(4, new FileInfo(storage.LogPath(id)).Length);
    }

    [Fact]
    public async Task TwoInstancesCannotReserveCapacityBeforePreviousWriterFinishes()
    {
        var first = new SchedulerManagedStorage(directory, 10);
        var second = new SchedulerManagedStorage(directory, 10);
        var id = Guid.NewGuid();
        using IDisposable held = await first.AcquireDefaultSaveAsync(first.LogPath(id), 4, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<IDisposable> waiting = second.AcquireDefaultSaveAsync(second.LogPath(Guid.NewGuid()), 7, timeout.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        await File.WriteAllBytesAsync(first.LogPath(id), new byte[4]);
        held.Dispose();
        await Assert.ThrowsAnyAsync<IOException>(async () => { using IDisposable lease = await waiting; });
    }

    [Fact]
    public async Task CorruptHistoryStopsPruningAndKeepsOriginalFile()
    {
        var storage = new SchedulerManagedStorage(directory, 10);
        SchedulerHistoryRecord completed = Capture(completed: true);
        await storage.SaveHistoryAsync(completed);
        await WriteLog(storage, completed.OccurrenceId, 10);
        string corrupt = Path.Combine(directory, "managed", "history", Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(corrupt, "{ broken");
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using IDisposable lease = await storage.AcquireDefaultSaveAsync(storage.LogPath(Guid.NewGuid()), 1, default);
            });
        Assert.Equal("{ broken", await File.ReadAllTextAsync(corrupt));
        Assert.True(File.Exists(storage.LogPath(completed.OccurrenceId)));
    }

    [Fact]
    public async Task ExplicitOwnPathIsRejectedButSiblingDirectoryIsAllowed()
    {
        var storage = new SchedulerManagedStorage(directory);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await storage.ValidateExplicitPathAsync(storage.LogPath(Guid.NewGuid()), default));
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await storage.ValidateExplicitPathAsync(Path.Combine(directory, "managed", "logs", "..", "history", "other.json"), default));
        await storage.ValidateExplicitPathAsync(directory + "-sibling.log", default);
    }

    [Fact]
    public async Task ExistingOutsideFileIsAllowedAndHardLinkToManagedLogIsRejected()
    {
        var storage = new SchedulerManagedStorage(Path.Combine(directory, "owned"));
        var id = Guid.NewGuid();
        await WriteLog(storage, id, 3);
        string outside = Path.Combine(directory, "outside.log");
        await File.WriteAllTextAsync(outside, "outside");
        await storage.ValidateExplicitPathAsync(outside, default);
        string alias = Path.Combine(directory, "alias.log");
        Assert.True(CreateHardLinkW(alias, storage.LogPath(id), 0),
            Marshal.GetLastWin32Error().ToString());
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await storage.ValidateExplicitPathAsync(alias, default));
        Assert.Equal(3, new FileInfo(alias).Length);
    }

    [Fact]
    public async Task DirectoryAliasAndDanglingLinkCannotBypassExplicitPathCheck()
    {
        string owned = Path.Combine(directory, "owned");
        Directory.CreateDirectory(owned);
        var storage = new SchedulerManagedStorage(owned);
        string alias = Path.Combine(directory, "alias");
        Directory.CreateSymbolicLink(alias, owned);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await storage.ValidateExplicitPathAsync(Path.Combine(alias, "not-created", "script.log"), default));
        string broken = Path.Combine(directory, "broken.log");
        File.CreateSymbolicLink(broken, Path.Combine(directory, "absent.log"));
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await storage.ValidateExplicitPathAsync(broken, default));
        Assert.Empty(Directory.EnumerateFiles(owned));
    }

    [Fact]
    public async Task LinkedManagedDirectoryStopsSaveAndPreservesTarget()
    {
        string owned = Path.Combine(directory, "owned");
        var storage = new SchedulerManagedStorage(owned);
        string target = Path.Combine(directory, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "keep.log"), "keep");
        Directory.CreateDirectory(Path.Combine(owned, "managed"));
        Directory.CreateSymbolicLink(Path.Combine(owned, "managed", "logs"), target);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                using IDisposable lease = await storage.AcquireDefaultSaveAsync(storage.LogPath(Guid.NewGuid()), 1, default);
            });
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "keep.log")));
    }

    [Fact]
    public async Task HistoryPruneRemovesOwnedLogAndRetriesAfterDeleteFailure()
    {
        var storage = new SchedulerManagedStorage(directory);
        SchedulerHistoryRecord oldest = Capture(completed: true);
        await storage.SaveHistoryAsync(oldest);
        await WriteLog(storage, oldest.OccurrenceId, 3);
        for (int index = 1; index < 100; index++)
        {
            await storage.SaveHistoryAsync(Capture(index, completed: true));
        }
        SchedulerHistoryRecord next = Capture(100, completed: true);
        using (var held = new FileStream(storage.LogPath(oldest.OccurrenceId), FileMode.Open,
            FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => storage.SaveHistoryAsync(next));
            Assert.Equal(101, storage.LoadHistory().Count);
        }
        await storage.SaveHistoryAsync(next);
        Assert.Equal(100, storage.LoadHistory().Count);
        Assert.DoesNotContain(storage.LoadHistory(), item => item.OccurrenceId == oldest.OccurrenceId);
        Assert.False(File.Exists(storage.LogPath(oldest.OccurrenceId)));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
}
