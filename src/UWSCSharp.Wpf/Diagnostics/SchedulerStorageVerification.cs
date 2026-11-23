using System.IO;
using System.Text.Json;
using UWSCSharp.Core;
using static UWSCSharp.Wpf.SchedulerVerification;
using SchedulerWindow = UWSCSharp.Wpf.MainWindow.SchedulerVerificationAccess;

namespace UWSCSharp.Wpf;

internal static class SchedulerStorageVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string logs = Path.Combine(directory, "logs");
        var storage = new SchedulerManagedStorage(logs);
        var checks = new List<string>();
        var windows = new List<SchedulerWindow>();
        var executions = new List<SchedulerRuntimeHost.Execution>();
        try
        {
            SchedulerWindow live = await RunPhase("live", key => $"PRINT \"{key}\"\n"
                + $"DIM f=FOPEN(\"{key}.ready\",F_WRITE8)\nFPUT(f,1)\nFCLOSE(f)\nSLEEP(30)");
            await WaitForRuntimeHost(() => new[] { "live-same", "live-worker" }
                .All(key => File.Exists(Path.Combine(directory, key + ".ready"))));
            await WaitForRuntimeHost(() => storage.LoadHistory().Count == 2
                && storage.LoadHistory().All(value => value.StartedAt is not null));
            Require(live.Host!.ActiveCount == 2, "live-two-starts-persisted");
            await WaitForRuntimeHost(() => live.Host.Executions
                .All(value => File.Exists(storage.LogPath(value.Dispatch.Request.OccurrenceId))));
            Require(live.Host.Executions.Select(value => storage.LogPath(value.Dispatch.Request.OccurrenceId))
                .Distinct().Count() == 2, "live-log-files-have-isolated-managed-paths");
            foreach (SchedulerRuntimeHost.Execution value in live.Host.Executions)
            {
                live.Host.Cancel(value.Dispatch.Request.OccurrenceId);
            }
            await WaitForRuntimeHost(() => live.Host.ActiveCount == 0);
            live.Host.StopAccepting();
            await WaitForRuntimeHost(() => !live.Host.IsPolling);
            await ClosePhase(live);
            Require(storage.LoadHistory().All(value => value.Outcome == SchedulerExecutionOutcome.Cancelled
                && value.ResourcesRecovered && value.EndedAt is not null),
                "live-drained-polling-stopped-and-final-cancel-results-persisted");

            string forbidden = Path.Combine(logs, "forbidden.log");
            SchedulerWindow reserved = await RunPhase("reserved", _ => "TRY\n"
                + $"OPTION LOGPATH=\"{forbidden}\"\nEXCEPT\nPRINT \"rejected\"\nENDTRY\n"
                + "OPTION LOGPATH=\"\"\nPRINT \"kept\"");
            await WaitForRuntimeHost(() => reserved.Host!.Executions.Count == 2
                && reserved.Host.ActiveCount == 0);
            await ClosePhase(reserved);
            foreach (SchedulerRuntimeHost.Execution value in reserved.Host!.Executions)
            {
                if (value.Result?.Outcome != SchedulerExecutionOutcome.Succeeded)
                {
                    throw new InvalidOperationException("管理領域のTRY拒否後の実行失敗: " + value.Result?.Diagnostic);
                }
                string text = File.ReadAllText(storage.LogPath(value.Dispatch.Request.OccurrenceId));
                Require(value.Result!.Outcome == SchedulerExecutionOutcome.Succeeded
                    && text.Contains("rejected") && text.Contains("kept"),
                    "reserved-try-rejected-and-default-reset-" + value.Dispatch.Request.Entry.Mode);
            }
            Require(!File.Exists(forbidden), "reserved-no-forbidden-file-created");

            string[] earlier = [.. executions.Select(value => storage.LogPath(value.Dispatch.Request.OccurrenceId))];
            string legacy = Path.Combine(logs, Guid.NewGuid().ToString("N") + ".log");
            using (var stream = new FileStream(legacy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(SchedulerManagedStorage.MaximumLogBytes);
            }
            SchedulerWindow quota = await RunPhase("quota", key => "PRINT \"omitted-before-switch\"\n"
                + $"OPTION LOGPATH=\"{Path.Combine(directory, key + ".outside.log")}\"\nPRINT \"continued-outside\"");
            await WaitForRuntimeHost(() => quota.Host!.Executions.Count == 2
                && quota.Host.ActiveCount == 0);
            await ClosePhase(quota);
            foreach (SchedulerRuntimeHost.Execution value in quota.Host!.Executions)
            {
                string text = File.ReadAllText(Path.Combine(directory, value.Dispatch.Request.Entry.Title + ".outside.log"));
                Require(value.Result!.Outcome == SchedulerExecutionOutcome.Succeeded
                    && value.Result.Diagnostic?.Contains("省略") == true
                    && text.Contains("continued-outside") && !text.Contains("omitted-before-switch"),
                    "quota-script-continues-with-outside-log-" + value.Dispatch.Request.Entry.Mode);
            }
            Require(new FileInfo(legacy).Length == SchedulerManagedStorage.MaximumLogBytes
                && earlier.All(File.Exists), "quota-legacy-and-earlier-managed-files-retained");
            IReadOnlyList<SchedulerHistoryRecord> records = storage.LoadHistory();
            Require(records.Count == 6 && executions.All(value => records.Any(record =>
                record.OccurrenceId == value.Dispatch.Request.OccurrenceId
                && record.AcceptedAt == value.Timeline.AcceptedAt
                && record.StartedAt == value.Timeline.StartedAt
                && record.ResultObservedAt == value.Timeline.ResultObservedAt
                && record.EndedAt == value.Timeline.EndedAt
                && record.Outcome == value.Result!.Outcome && record.ExitCode == value.Result.ExitCode
                && record.ResourcesRecovered == value.Result.ResourcesRecovered)),
                "all-six-history-results-match-parent-observations");
            Require(executions.All(value => records.Any(record =>
                record.OccurrenceId == value.Dispatch.Request.OccurrenceId
                && record.Title == value.Dispatch.Request.Entry.Title
                && record.Path == value.Dispatch.Request.Entry.Path
                && record.Generation == value.Dispatch.Generation
                && record.Arguments.SequenceEqual(["", "a b", "日本語"]))),
                "all-six-snapshots-and-ordered-arguments-retained");
            Require(windows.All(window => window.IsClosed && window.Host!.CanClose),
                "all-three-windows-released-monitor-and-resources");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                executions = 6,
                checks
            }));
            return 0;
        }
        finally
        {
            foreach (SchedulerWindow window in windows)
            {
                if (window.Window.IsVisible)
                {
                    window.Window.Close();
                }
            }
        }

        async Task<SchedulerWindow> RunPhase(string phase, Func<string, string> source)
        {
            var window = new MainWindow(false, confirmDiscard: () => true) { ShowActivated = false };
            var access = new SchedulerWindow(window);
            windows.Add(access);
            SchedulerEntry[] entries = [Entry("same", SchedulerExecutionMode.SameProcess),
                Entry("worker", SchedulerExecutionMode.SeparateProcess)];
            string settings = Path.Combine(directory, phase + ".json");
            SchedulerSettingsStore.Save(settings, new SchedulerSettings(entries, enabled: true), null);
            window.Show();
            await window.RefreshInputCleanupAsync();
            await window.InitializeSchedulerAsync(settings, Path.Combine(directory, phase + ".monitor"), logs);
            return access;

            SchedulerEntry Entry(string name, SchedulerExecutionMode mode)
            {
                string key = phase + "-" + name;
                string path = Path.Combine(directory, key + ".uws");
                string script = source(key);
                ScriptCompiler.Compile(script);
                WriteRuntimeHostScript(path, script);
                return new SchedulerEntry(Guid.NewGuid(), key, path, ["", "a b", "日本語"],
                    mode, new SchedulerStartupCondition(), directory);
            }
        }

        async Task ClosePhase(SchedulerWindow window)
        {
            RequireStop(await window.Host!.StopAsync(TimeSpan.FromSeconds(20)));
            executions.AddRange(window.Host.Executions);
            window.Window.Close();
            await WaitForRuntimeHost(() => window.IsClosed);
        }

        static void RequireStop(bool stopped)
        {
            if (!stopped)
            {
                throw new InvalidOperationException("履歴診断の実行資源を回収できません。");
            }
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }
}
