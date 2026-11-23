using System.Text.Json;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 一時ファイルと履歴だけを操作し、画面や実入力を使いません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerHistoryStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-history-" + Guid.NewGuid());
    private static readonly DateTimeOffset Accepted = new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

    public SchedulerHistoryStoreTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, true);

    private string FilePath(Guid id) => Path.Combine(directory, id.ToString("N") + ".json");

    private SchedulerHistoryRecord Capture()
    {
        var entry = new SchedulerEntry(Guid.NewGuid(), "登録時の題名",
            Path.Combine(directory, "main.uws"), ["", "a b", "日本語"],
            SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), directory);
        return SchedulerHistoryRecord.Capture(new SchedulerDispatch(Guid.NewGuid(),
            new SchedulerExecutionRequest(Guid.NewGuid(), entry, Accepted)));
    }

    [Fact]
    public void SnapshotRetainsArgumentsAndAllObservedTimesAcrossReload()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord accepted = Capture();
        store.Save(accepted);
        SchedulerHistoryRecord started = accepted.MarkStarted(Accepted.AddSeconds(1));
        store.Save(started);
        SchedulerHistoryRecord completed = started.Complete(
            SchedulerExecutionResult.Completed(7, true, false, true, "実行診断"), Accepted.AddSeconds(2), endedConfirmed: true);
        store.Save(completed);
        SchedulerHistoryRecord loaded = Assert.Single(store.Load());
        Assert.Equal(accepted.OccurrenceId, loaded.OccurrenceId);
        Assert.Equal(accepted.Generation, loaded.Generation);
        Assert.Equal(accepted.EntryId, loaded.EntryId);
        Assert.Equal(accepted.Title, loaded.Title);
        Assert.Equal(accepted.Path, loaded.Path);
        Assert.Equal(["", "a b", "日本語"], loaded.Arguments);
        Assert.Equal(accepted.Mode, loaded.Mode);
        Assert.Equal(Accepted, loaded.AcceptedAt);
        Assert.Equal(Accepted.AddSeconds(1), loaded.StartedAt);
        Assert.Equal(Accepted.AddSeconds(2), loaded.EndedAt);
        Assert.Equal(SchedulerExecutionOutcome.Failed, loaded.Outcome);
        Assert.Equal(7, loaded.ExitCode);
        Assert.Equal("実行診断", loaded.Diagnostic);
        Assert.True(loaded.ResourcesRecovered);
        Assert.False(loaded.IsProtected);
    }

    [Fact]
    public void LateStartAndCompletionCannotRewriteFinishedSnapshot()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord accepted = Capture();
        SchedulerHistoryRecord finished = accepted.MarkStarted(Accepted).Complete(
            SchedulerExecutionResult.Completed(0, true, false, true), Accepted.AddSeconds(1), endedConfirmed: true);
        store.Save(finished);
        store.Save(accepted);
        Assert.Equal(finished, finished.MarkStarted(Accepted.AddMinutes(1)));
        Assert.Equal(finished, finished.Complete(SchedulerExecutionResult.FailedToStart("late"), Accepted, endedConfirmed: true));
        Assert.Equal(SchedulerExecutionOutcome.Succeeded, Assert.Single(store.Load()).Outcome);
        Assert.Throws<InvalidDataException>(() => store.Save(finished with
        {
            Revision = finished.Revision + 1,
            Outcome = SchedulerExecutionOutcome.Failed,
            ExitCode = 7
        }));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("entry")]
    [InlineData("title")]
    [InlineData("arguments")]
    public void ReceiptDefinitionCannotBeReplacedByCurrentSettings(string change)
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Capture();
        store.Save(record);
        byte[] before = File.ReadAllBytes(FilePath(record.OccurrenceId));
        SchedulerHistoryRecord candidate = change switch
        {
            "generation" => record with { Generation = Guid.NewGuid() },
            "entry" => record with { EntryId = Guid.NewGuid() },
            "title" => record with { Title = "変更後" },
            _ => record with { Arguments = ["different"] }
        };
        Assert.Throws<InvalidDataException>(() => store.Save(candidate with { Revision = 1 }));
        Assert.Equal(before, File.ReadAllBytes(FilePath(record.OccurrenceId)));
    }

    [Fact]
    public void PruningKeepsHundredRecoveredRecordsAndEveryProtectedRecord()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord pending = Capture();
        SchedulerHistoryRecord unknown = Capture().Complete(
            SchedulerExecutionResult.Unknown(true, null, false, "回収未確認"), Accepted, endedConfirmed: false);
        store.Save(pending);
        store.Save(unknown);
        string sentinel = Path.Combine(directory, "schedule.json");
        File.WriteAllText(sentinel, "do not modify settings");
        SchedulerHistoryRecord? oldest = null;
        for (int index = 0; index < 102; index++)
        {
            SchedulerHistoryRecord value = Capture().Complete(
                SchedulerExecutionResult.FailedToStart("failed"), Accepted.AddSeconds(index), endedConfirmed: true);
            oldest ??= value;
            store.Save(value);
        }
        IReadOnlyList<SchedulerHistoryRecord> records = store.Load();
        Assert.Equal(102, records.Count);
        Assert.Equal(100, records.Count(value => !value.IsProtected));
        Assert.Contains(records, value => value.OccurrenceId == pending.OccurrenceId);
        Assert.Contains(records, value => value.OccurrenceId == unknown.OccurrenceId);
        Assert.False(File.Exists(FilePath(oldest!.OccurrenceId)));
        Assert.Equal("do not modify settings", File.ReadAllText(sentinel));
    }

    [Fact]
    public void SameRevisionRetriesPruningAfterPostCommitFailure()
    {
        var store = new SchedulerHistoryStore(directory);
        for (int index = 0; index < 100; index++)
        {
            store.Save(Capture().Complete(SchedulerExecutionResult.FailedToStart("failed"),
                Accepted.AddSeconds(index), endedConfirmed: true));
        }
        string broken = FilePath(Guid.NewGuid());
        File.WriteAllText(broken, "{");
        SchedulerHistoryRecord latest = Capture().Complete(
            SchedulerExecutionResult.FailedToStart("latest"), Accepted.AddHours(1), endedConfirmed: true);
        Assert.ThrowsAny<JsonException>(() => store.Save(latest));
        Assert.True(File.Exists(FilePath(latest.OccurrenceId)));
        File.Delete(broken);
        store.Save(latest);
        Assert.Equal(100, store.Load().Count);
    }

    [Fact]
    public void SameRevisionWithDifferentContentIsRejected()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Capture();
        store.Save(record);
        Assert.Throws<InvalidDataException>(() => store.Save(record with { Started = true }));
        Assert.False(Assert.Single(store.Load()).Started);
    }

    [Fact]
    public void ClockRollbackIsPreservedRatherThanInventingLaterTimes()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Capture().MarkStarted(Accepted.AddHours(1)).Complete(
            SchedulerExecutionResult.Completed(130, true, true, true), Accepted.AddHours(-1), endedConfirmed: true);
        store.Save(record);
        Assert.Equal(Accepted.AddHours(-1), Assert.Single(store.Load()).EndedAt);
    }

    [Fact]
    public void UnknownRunningProcessHasResultTimeWithoutInventedEndTime()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Capture().MarkStarted(Accepted).Complete(
            SchedulerExecutionResult.Unknown(false, null, false, "unconfirmed"),
            Accepted.AddSeconds(1), endedConfirmed: false);
        store.Save(record);
        SchedulerHistoryRecord loaded = Assert.Single(store.Load());
        Assert.Equal(Accepted.AddSeconds(1), loaded.ResultObservedAt);
        Assert.True(loaded.Started);
        Assert.Null(loaded.EndedAt);
        Assert.True(loaded.IsProtected);
        Assert.Same(record, record.Complete(
            SchedulerExecutionResult.Completed(0, true, false, true),
            Accepted.AddSeconds(2), endedConfirmed: true));
    }

    [Fact]
    public void LockConflictKeepsExistingRecord()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Capture();
        store.Save(record);
        using var ownership = new FileStream(Path.Combine(directory, ".history.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => store.Save(record.MarkStarted(Accepted)));
        Assert.Null(Assert.Single(store.Load()).StartedAt);
    }

    [Fact]
    public void MissingFieldsAndDuplicatePropertiesAreRejected()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Capture();
        store.Save(record);
        string path = FilePath(record.OccurrenceId);
        string original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("\"version\":3", "\"version\":3,\"version\":3", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => store.Load());
        File.WriteAllText(path, "{\"version\":3,\"window\":null}");
        Assert.ThrowsAny<JsonException>(() => store.Load());
    }

    [Fact]
    public void FileNameAndOccurrenceMismatchIsRejected()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Capture();
        store.Save(record);
        File.Move(FilePath(record.OccurrenceId), FilePath(Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void OversizedInputIsRejectedBeforeReadingPayload()
    {
        using (FileStream file = File.Create(FilePath(Guid.NewGuid())))
        {
            file.SetLength(SchedulerHistoryStore.MaximumRecordBytes + 1L);
        }
        Assert.Throws<InvalidDataException>(() => new SchedulerHistoryStore(directory).Load());
    }

    [Fact]
    public void EscapedOutputCannotExceedBoundOrReplacePreviousRecord()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Capture();
        store.Save(record);
        byte[] before = File.ReadAllBytes(FilePath(record.OccurrenceId));
        string text = new('<', 1_000_000);
        Assert.Throws<InvalidDataException>(() => store.Save(Capture() with { Arguments = [text, text, text] }));
        Assert.Equal(before, File.ReadAllBytes(FilePath(record.OccurrenceId)));
        Assert.Single(store.Load());
    }
}
