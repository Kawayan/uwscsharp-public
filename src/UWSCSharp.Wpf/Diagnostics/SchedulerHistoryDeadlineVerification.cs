using System.IO;
using System.Text.Json;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerHistoryDeadlineVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        string logs = Path.Combine(directory, "logs");
        string settings = Path.Combine(directory, "schedule.json");
        var clock = new HistoryDeadlineClock();
        var entry = new SchedulerEntry(Guid.NewGuid(), "保存期限",
            Path.Combine(directory, "unused.uws"), [], SchedulerExecutionMode.SameProcess,
            new SchedulerOneShotCondition(new DateOnly(2027, 1, 1), new TimeOnly(0, 1)), directory);
        SchedulerSettingsStore.Save(settings, new SchedulerSettings([entry], enabled: true), null);
        var storage = new SchedulerManagedStorage(logs);
        var previous = SchedulerHistoryRecord.Capture(new SchedulerDispatch(
            Guid.NewGuid(), new SchedulerExecutionRequest(Guid.NewGuid(), entry, clock.Now)));
        await storage.SaveHistoryAsync(previous);
        string previousPath = Path.Combine(logs, "managed", "history", previous.OccurrenceId.ToString("N") + ".json");
        byte[] previousBytes = await File.ReadAllBytesAsync(previousPath);
        IDisposable ownership = await storage.AcquireDefaultSaveAsync(storage.LogPath(Guid.NewGuid()), 0, default);
        var checks = new List<string>();
        var errors = new List<Exception>();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var main = new MainWindow(false, confirmDiscard: () => true, reportError: errors.Add,
            launcherSettingsPath: Path.Combine(directory, "launcher.json"))
        {
            ShowActivated = false
        };
        var access = new MainWindow.SchedulerVerificationAccess(main);
        bool closeReady = false;
        string? closeStatus = null;
        Exception? persistenceError = null;
        main.Closed += (_, _) =>
        {
            closeReady = access.IsCloseReady && !access.IsExitHeld
                && access.Host!.CanClose && access.Host.ActiveCount == 0;
            closeStatus = access.StatusText;
            persistenceError = access.Host!.PersistenceError;
            closed.TrySetResult();
        };
        try
        {
            main.Show();
            await main.RefreshInputCleanupAsync();
            await access.InitializeAsync(settings,
                Path.Combine(directory, "monitor"), logs, clock,
                async (_, _, started) =>
                {
                    started();
                    await finish.Task;
                    return SchedulerExecutionResult.Completed(
                        7, true, false, true);
                });
            SchedulerRuntimeHost host = access.Host!;
            Require(host.IsActive && host.OwnsMonitor, "normal-window-monitor-ready");
            clock.Now = clock.Now.AddMinutes(1);
            host.Poll();
            await WaitForRuntimeHost(() => host.IsSavingHistory && host.Executions.Count == 1);
            Require(!File.Exists(Path.Combine(logs, "managed", "history",
                host.Executions.Single().Dispatch.Request.OccurrenceId.ToString("N") + ".json")),
                "history-writer-entered-under-actual-storage-lock");
            finish.TrySetResult();
            await WaitForRuntimeHost(() => host.Executions.Single().Finished.Task.IsCompleted);
            SchedulerRuntimeHost.Execution execution = host.Executions.Single();
            Require(execution.Result is { ExitCode: 7, ResourcesRecovered: true }
                && execution.ResultSaved && host.ActiveCount == 0
                && SchedulerSettingsStore.Load(settings).Settings.OneShotStates.Single().Status == SchedulerOneShotStatus.Failed,
                "result-and-one-shot-complete-independently-of-history-save");
            main.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Require(closeReady && persistenceError is TimeoutException
                && persistenceError.Message.Contains("5秒", StringComparison.Ordinal),
                "five-second-history-deadline-reports-error-with-close-ready");
            Require(closeStatus?.Contains("予約履歴の保存診断", StringComparison.Ordinal) == true
                && closeStatus.Contains("5秒", StringComparison.Ordinal) && errors.Count == 0,
                "persistence-diagnostic-shown-before-normal-close");
            byte[] currentBytes = await File.ReadAllBytesAsync(previousPath);
            Require(previousBytes.SequenceEqual(currentBytes)
                && Directory.GetFiles(Path.GetDirectoryName(previousPath)!, "*.json").Length == 1,
                "old-history-preserved-and-no-false-new-history");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                checks
            }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(receipt + ".conditions", JsonSerializer.Serialize(new
            {
                checks,
                closeReady,
                closeStatus,
                persistence_error = persistenceError?.ToString(),
                error = error.ToString()
            }));
            throw;
        }
        finally
        {
            ownership.Dispose();
            finish.TrySetResult();
            if (!closed.Task.IsCompleted && access.Host is not null)
            {
                await access.Host.StopAsync(TimeSpan.FromSeconds(20));
            }
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }

    private sealed class HistoryDeadlineClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
