using System.IO;
using System.Text;
using System.Text.Json;
using static UWSCSharp.Wpf.SchedulerVerification;
using static UWSCSharp.Wpf.SchedulerWorkerDenialVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerWorkerLogDenialVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        var checks = new List<string>();
        foreach (string mode in new[] { "success", "failure", "cancel" })
        {
            string root = Path.Combine(directory, mode);
            Directory.CreateDirectory(root);
            string logs = Path.Combine(root, "logs");
            string settings = Path.Combine(root, "schedule.json");
            string script = Path.Combine(root, "worker.uws");
            string source = "PRINT \"before\"\nDIM f=FOPEN(\"ready\",F_WRITE8)\nFCLOSE(f)\n"
                + "WHILE !FOPEN(\"go\",F_EXISTS)\nSLEEP(0.02)\nWEND\nPRINT \"after\"\n"
                + "f=FOPEN(\"continued\",F_WRITE8)\nFCLOSE(f)\n"
                + (mode == "failure" ? "PRINT missing_variable" : mode == "cancel"
                    ? "WHILE TRUE\nSLEEP(0.02)\nWEND" : "");
            File.WriteAllText(script, source, new UTF8Encoding(true));
            var clock = new DenialClock();
            SaveSettings(settings, script, root, mode);
            var errors = new List<Exception>();
            MainWindow main = CreateWindow(root, errors);
            var access = new MainWindow.SchedulerVerificationAccess(main);
            SchedulerRuntimeHost? host = null;
            string? path = null;
            try
            {
                host = await InitializeWorkerAsync(
                    access, settings, root, logs, clock);
                SchedulerRuntimeHost.Execution execution =
                    await WaitForWorkerAsync(host, clock, root);
                var storage = new SchedulerManagedStorage(logs);
                path = storage.LogPath(execution.Dispatch.Request.OccurrenceId);
                await WaitForRuntimeHost(() => File.Exists(path));
                Require(host.ActiveCount == 1 && host.OwnedProcessIds.Count == 1
                    && !host.OwnedProcessIds.Contains((uint)Environment.ProcessId),
                    "actual-worker-and-periodic-log-exist");
                byte[] previous;
                using (IDisposable ownership = await storage.AcquireDefaultSaveAsync(path, 0, default))
                {
                    previous = await ReadSharedBytesAsync(path);
                    int denied = ProbeReadOnlyReplacement(path);
                    Require(denied == 5 && Encoding.UTF8.GetString(previous).Contains("before", StringComparison.Ordinal),
                        "existing-default-log-replacement-actually-denied-with-error-five");
                }
                File.WriteAllText(Path.Combine(root, "go"), "go");
                await WaitForRuntimeHost(() =>
                    File.Exists(Path.Combine(root, "continued")));
                Require(mode != "cancel" || host.Cancel(execution.Dispatch.Request.OccurrenceId),
                    "script-continues-after-output-and-normal-cancel-is-accepted");
                await WaitForRuntimeHost(() =>
                    execution.Finished.Task.IsCompleted);
                SchedulerExecutionOutcome expected = mode == "success" ? SchedulerExecutionOutcome.Succeeded
                    : mode == "failure" ? SchedulerExecutionOutcome.Failed : SchedulerExecutionOutcome.Cancelled;
                int code = mode == "success" ? 0 : mode == "failure" ? 1 : 130;
                Require(execution.Result is { Started: true, ResourcesRecovered: true }
                    && execution.Result.Outcome == expected && execution.Result.ExitCode == code
                    && execution.Result.Diagnostic?.Contains("予約ログの保存", StringComparison.Ordinal) == true,
                    "actual-worker-result-retains-outcome-and-log-failure-diagnostic");
                string historyPath = Path.Combine(logs, "managed", "history",
                    execution.Dispatch.Request.OccurrenceId.ToString("N") + ".json");
                await WaitForRuntimeHost(() => File.Exists(historyPath)
                    && storage.ReadHistoryRecord(execution.Dispatch.Request.OccurrenceId).Outcome is not null
                    && !host.IsSavingHistory);
                SchedulerHistoryRecord saved = storage.ReadHistoryRecord(execution.Dispatch.Request.OccurrenceId);
                Require(saved.Outcome == expected && saved.ExitCode == code && saved.Started
                    && saved.ResourcesRecovered && !saved.IsProtected
                    && saved.Diagnostic?.Contains("予約ログの保存", StringComparison.Ordinal) == true
                    && execution.ResultSaved && SchedulerSettingsStore.Load(settings).Settings.OneShotStates.Single().Status
                        == (mode == "success" ? SchedulerOneShotStatus.Succeeded : SchedulerOneShotStatus.Failed),
                    "history-and-consumed-one-shot-retain-real-worker-result");
                byte[] current = await ReadSharedBytesAsync(path);
                Require(previous.SequenceEqual(current)
                    && Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0,
                    "denied-log-keeps-original-bytes-and-cleans-temporary-files");
                bool recovered = host.ActiveCount == 0 && access.ScheduledCount == 0
                    && host.OwnedProcessIds.Count == 0 && errors.Count == 0;
                main.Close();
                await WaitForRuntimeHost(() => access.IsClosed);
                Require(recovered && host.CanClose, "worker-drained-and-main-window-closes-normally");
            }
            catch (Exception error)
            {
                File.WriteAllText(receipt + ".conditions", JsonSerializer.Serialize(new
                {
                    mode,
                    checks,
                    active = host?.ActiveCount,
                    persistence_error = host?.PersistenceError?.ToString(),
                    results = host?.Executions.Select(execution => new
                    {
                        outcome = execution.Result?.Outcome.ToString(),
                        code = execution.Result?.ExitCode,
                        recovered = execution.Result?.ResourcesRecovered,
                        diagnostic = execution.Result?.Diagnostic
                    }).ToArray(),
                    error = error.ToString()
                }));
                throw;
            }
            finally
            {
                await RestoreAsync(path, main, access, host);
            }

            void Require(bool condition, string name)
                => VerificationAssert.Require(checks, condition,
                    mode + ": " + name);
        }
        return await WriteReceiptAsync(receipt, checks);
    }
}
