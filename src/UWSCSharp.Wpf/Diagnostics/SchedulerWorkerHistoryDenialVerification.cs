using System.IO;
using System.Text;
using System.Text.Json;
using static UWSCSharp.Wpf.SchedulerVerification;
using static UWSCSharp.Wpf.SchedulerWorkerDenialVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerWorkerHistoryDenialVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        var checks = new List<string>();
        foreach (string mode in new[] { "retry", "close" })
        {
            string root = Path.Combine(directory, mode);
            Directory.CreateDirectory(root);
            string logs = Path.Combine(root, "logs");
            string settings = Path.Combine(root, "schedule.json");
            string script = Path.Combine(root, "worker.uws");
            File.WriteAllText(script, "PRINT \"one\"\nDIM f=FOPEN(\"ready\",F_WRITE8)\nFCLOSE(f)\n"
                + "WHILE !FOPEN(\"go\",F_EXISTS)\nSLEEP(0.02)\nWEND\nPRINT \"two\"\n", new UTF8Encoding(true));
            var clock = new DenialClock();
            SaveSettings(settings, script, root, mode);
            var errors = new List<Exception>();
            MainWindow main = CreateWindow(root, errors);
            var access = new MainWindow.SchedulerVerificationAccess(main);
            SchedulerRuntimeHost? host = null;
            string? historyPath = null;
            try
            {
                host = await InitializeWorkerAsync(
                    access, settings, root, logs, clock);
                SchedulerRuntimeHost.Execution execution =
                    await WaitForWorkerAsync(host, clock, root);
                var storage = new SchedulerManagedStorage(logs);
                Guid id = execution.Dispatch.Request.OccurrenceId;
                historyPath = Path.Combine(logs, "managed", "history", id.ToString("N") + ".json");
                await WaitForRuntimeHost(() =>
                    File.Exists(historyPath)
                    && storage.ReadHistoryRecord(id).Started);
                Require(host.ActiveCount == 1 && host.OwnedProcessIds.Count == 1
                    && storage.ReadHistoryRecord(id).Revision == 1,
                    "real-worker-started-and-started-history-persisted");
                byte[] previous;
                using (IDisposable ownership = await storage.AcquireDefaultSaveAsync(storage.LogPath(id), 0, default))
                {
                    previous = await ReadSharedBytesAsync(historyPath);
                    int denied = ProbeReadOnlyReplacement(historyPath);
                    Require(denied == 5, "started-history-replacement-actually-denied");
                }
                File.WriteAllText(Path.Combine(root, "go"), "go");
                await WaitForRuntimeHost(() =>
                    execution.Finished.Task.IsCompleted);
                Require(execution.Result is
                {
                    Outcome: SchedulerExecutionOutcome.Succeeded, ExitCode: 0,
                    Started: true, ResourcesRecovered: true
                }
                    && execution.ResultSaved && host.ActiveCount == 0 && access.ScheduledCount == 0
                    && host.OwnedProcessIds.Count == 0
                    && SchedulerSettingsStore.Load(settings).Settings.OneShotStates.Single().Status == SchedulerOneShotStatus.Succeeded,
                    "actual-worker-success-and-consumed-one-shot-survive-history-failure");
                await WaitForRuntimeHost(() =>
                    host.PersistenceError is UnauthorizedAccessException);
                access.RefreshStatus();
                byte[] failedBytes = await ReadSharedBytesAsync(historyPath);
                Require((host.PersistenceError!.HResult & 0xffff) == 5 && previous.SequenceEqual(failedBytes)
                    && access.StatusText.Contains("予約履歴の保存診断", StringComparison.Ordinal)
                    && main.IsVisible && !access.IsExitHeld,
                    "actual-history-access-error-preserves-old-bytes-and-visible-diagnostic");
                if (mode == "retry")
                {
                    File.SetAttributes(historyPath, File.GetAttributes(historyPath) & ~FileAttributes.ReadOnly);
                    await WaitForRuntimeHost(() =>
                        storage.ReadHistoryRecord(id).Outcome
                            == SchedulerExecutionOutcome.Succeeded);
                    SchedulerHistoryRecord saved = storage.ReadHistoryRecord(id);
                    Require(saved.Revision == 2 && saved.ExitCode == 0 && saved.Started && saved.ResourcesRecovered
                        && !saved.IsProtected && saved.EndedAt is not null,
                        "periodic-journal-retry-saves-latest-real-worker-completion");
                }
                else
                {
                    SchedulerHistoryRecord saved = storage.ReadHistoryRecord(id);
                    Require(saved.Revision == 1 && saved.Started && saved.Outcome is null
                        && !saved.ResourcesRecovered && saved.IsProtected,
                        "denied-history-retains-older-protected-snapshot-without-false-completion");
                }
                string output = Encoding.UTF8.GetString(await ReadSharedBytesAsync(storage.LogPath(id)));
                Require(output.Contains("two", StringComparison.Ordinal)
                    && execution.Result is { } result
                    && result.Diagnostic?.Contains("予約ログの保存", StringComparison.Ordinal) != true
                    && Directory.GetFiles(Path.GetDirectoryName(historyPath)!, "*.tmp").Length == 0,
                    "worker-default-log-remains-writable-and-history-temp-files-clean");
                main.Close();
                await WaitForRuntimeHost(() => access.IsClosed);
                byte[] closedBytes = await ReadSharedBytesAsync(historyPath);
                Require(host.CanClose && errors.Count == 0
                    && (mode == "retry" || previous.SequenceEqual(closedBytes)),
                    "main-window-closes-normally-with-case-specific-history-preserved");
            }
            catch (Exception error)
            {
                File.WriteAllText(receipt + ".conditions", JsonSerializer.Serialize(new
                {
                    mode,
                    checks,
                    active = host?.ActiveCount,
                    persistence_error = host?.PersistenceError?.ToString(),
                    status = access.StatusText,
                    error = error.ToString()
                }));
                throw;
            }
            finally
            {
                await RestoreAsync(historyPath, main, access, host);
            }

            void Require(bool condition, string name)
                => VerificationAssert.Require(checks, condition,
                    mode + ": " + name);
        }
        return await WriteReceiptAsync(receipt, checks);
    }
}
