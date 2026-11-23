using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

/// <summary>
/// 保存の代替だけを使い、画面・入力・ファイルを操作しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerHistoryJournalTests
{
    private static readonly DateTimeOffset Accepted = new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

    private static SchedulerHistoryRecord Capture(int seconds = 0)
    {
        var entry = new SchedulerEntry(Guid.NewGuid(), "受付原稿",
            Path.Combine(Path.GetTempPath(), "main.uws"), ["", "a b"],
            SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), Path.GetTempPath());
        return SchedulerHistoryRecord.Capture(new SchedulerDispatch(Guid.NewGuid(),
            new SchedulerExecutionRequest(Guid.NewGuid(), entry, Accepted.AddSeconds(seconds))));
    }

    [Fact]
    public async Task UpdatesDuringSaveAreDrainedWithoutOverwritingNewSnapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<SchedulerHistoryRecord>();
        var journal = new SchedulerHistoryJournal(async (record, token) =>
        {
            if (record.Revision == 0)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            saved.Add(record);
        });
        SchedulerHistoryRecord accepted = Capture();
        journal.Enqueue(accepted);
        Assert.True(journal.HasPending);
        Task writing = journal.FlushAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        SchedulerHistoryRecord complete = accepted.MarkStarted(Accepted.AddSeconds(1))
            .Complete(SchedulerExecutionResult.Completed(0, true, false, true),
                Accepted.AddSeconds(2), endedConfirmed: true);
        journal.Enqueue(complete);
        journal.Enqueue(accepted);
        Assert.True(journal.HasPending);
        release.SetResult();
        await writing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(journal.HasPending);
        Assert.Equal([0, 2], saved.Select(record => record.Revision));
        Assert.Equal(Accepted.AddSeconds(1), saved[^1].StartedAt);
        Assert.Equal(["", "a b"], saved[^1].Arguments);
        Assert.Equal(SchedulerExecutionOutcome.Succeeded, saved[^1].Outcome);
    }

    [Fact]
    public async Task FailedSaveIsRetriedAndDoesNotChangeResult()
    {
        bool fail = true;
        int attempts = 0;
        SchedulerHistoryRecord? saved = null;
        var journal = new SchedulerHistoryJournal((record, _) =>
        {
            attempts++;
            if (fail)
            {
                throw new IOException("保存拒否");
            }
            saved = record;
            return Task.CompletedTask;
        });
        SchedulerHistoryRecord complete = Capture().Complete(
            SchedulerExecutionResult.Completed(7, true, false, true),
            Accepted.AddSeconds(1), endedConfirmed: true);
        journal.Enqueue(complete);
        await journal.FlushAsync();
        Assert.IsType<IOException>(journal.LastError);
        Assert.True(journal.HasPending);
        fail = false;
        await journal.FlushAsync();
        Assert.False(journal.HasPending);
        Assert.Equal(2, attempts);
        Assert.Equal(complete, saved);
        Assert.Equal(SchedulerExecutionOutcome.Failed, saved!.Outcome);
        Assert.Equal(7, saved.ExitCode);
    }

    [Fact]
    public async Task CompletedPendingLimitKeepsRunningAndUnrecoveredRecords()
    {
        var saved = new List<SchedulerHistoryRecord>();
        var journal = new SchedulerHistoryJournal((record, _) =>
        {
            saved.Add(record);
            return Task.CompletedTask;
        });
        SchedulerHistoryRecord running = Capture();
        SchedulerHistoryRecord unknown = Capture().Complete(
            SchedulerExecutionResult.Unknown(true, null, false, "回収未確認"),
            Accepted, endedConfirmed: false);
        journal.Enqueue(running);
        journal.Enqueue(unknown);
        for (int index = 0; index < 101; index++)
        {
            journal.Enqueue(Capture(index).Complete(
                SchedulerExecutionResult.Completed(0, true, false, true),
                Accepted.AddSeconds(index + 1), endedConfirmed: true));
        }
        await journal.FlushAsync();
        Assert.Equal(102, saved.Count);
        Assert.Contains(saved, record => record.OccurrenceId == running.OccurrenceId);
        Assert.Contains(saved, record => record.OccurrenceId == unknown.OccurrenceId);
        Assert.NotNull(journal.LastError);
    }
}
