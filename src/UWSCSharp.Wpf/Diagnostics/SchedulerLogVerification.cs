using System.IO;
using System.Text;
using System.Text.Json;
using UWSCSharp.Core;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerLogVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        var main = new MainWindow(false, confirmDiscard: () => true) { ShowActivated = false };
        var bad = new MainWindow(false, confirmDiscard: () => true) { ShowActivated = false };
        var mainAccess = new MainWindow.SchedulerVerificationAccess(main);
        var badAccess = new MainWindow.SchedulerVerificationAccess(bad);
        string logs = Path.Combine(directory, "logs");
        string blockedLogs = Path.Combine(directory, "blocked-logs");
        File.WriteAllText(blockedLogs, "blocked");
        try
        {
            SchedulerEntry[] entries = [Entry("same", SchedulerExecutionMode.SameProcess, large: true),
                Entry("separate", SchedulerExecutionMode.SeparateProcess, large: true)];
            await Initialize(main, "good", logs, entries);
            SchedulerRuntimeHost host = mainAccess.Host!;
            await WaitForRuntimeHost(() => Ready("same") && Ready("separate"));
            Require(host.ActiveCount == 2, "both-executions-running-after-large-output");
            await WaitForRuntimeHost(() => LogPaths().All(File.Exists));
            Require(host.Executions.All(value => value.Result is null),
                "periodic-log-written-before-completion");
            Require(LogPaths().All(path => new FileInfo(path).Length is > 0 and <= 4 * 1024 * 1024),
                "both-running-logs-within-four-mib");
            SchedulerRuntimeHost.Execution same = host.Executions.Single(value =>
                value.Dispatch.Request.Entry.Mode == SchedulerExecutionMode.SameProcess);
            Require(host.Cancel(same.Dispatch.Request.OccurrenceId), "cancel-same-after-periodic-save");
            await WaitForRuntimeHost(() => same.Result is not null);
            Require(same.Result!.Outcome == SchedulerExecutionOutcome.Cancelled
                && same.Result.ResourcesRecovered && same.Result.Diagnostic!.Contains("容量制限"),
                "cancelled-result-retains-byte-omission-diagnostic");
            File.WriteAllText(Path.Combine(directory, "separate.go"), "go");
            await WaitForRuntimeHost(() => host.ActiveCount == 0);
            SchedulerRuntimeHost.Execution separate = host.Executions.Single(value =>
                value.Dispatch.Request.Entry.Mode == SchedulerExecutionMode.SeparateProcess);
            Require(separate.Result!.Outcome == SchedulerExecutionOutcome.Succeeded
                && separate.Result.Diagnostic!.Contains("容量制限"),
                "successful-worker-retains-byte-omission-diagnostic");
            foreach (string path in LogPaths())
            {
                byte[] bytes = File.ReadAllBytes(path);
                Require(bytes.Length <= 4 * 1024 * 1024
                    && new UTF8Encoding(false, true).GetString(bytes).Contains('界'),
                    "valid-bounded-final-log-" + Path.GetFileNameWithoutExtension(path));
            }

            SchedulerEntry failure = Entry("error", SchedulerExecutionMode.SeparateProcess);
            WriteRuntimeHostScript(failure.Path, "PRINT missing_variable");
            ScriptCompiler.CompileFile(failure.Path);
            await Initialize(bad, "bad", blockedLogs,
                [Entry("bad-same", SchedulerExecutionMode.SameProcess),
                    Entry("bad-separate", SchedulerExecutionMode.SeparateProcess), failure]);
            await WaitForRuntimeHost(() => Ready("bad-same") && Ready("bad-separate")
                && badAccess.Host!.Executions.Any(value => value.Result?.Outcome == SchedulerExecutionOutcome.Failed));
            Require(await badAccess.Host!.StopAsync(TimeSpan.FromSeconds(20)),
                "save-failure-does-not-prevent-cooperative-stop-and-recovery");
            SchedulerRuntimeHost.Execution[] cancelled = [.. badAccess.Host.Executions.Where(value =>
                value.Dispatch.Request.Entry.Id != failure.Id)];
            Require(cancelled.Length == 2 && cancelled.All(value => value.Result!.Outcome
                == SchedulerExecutionOutcome.Cancelled && value.Result.ExitCode == 130
                && value.Result.ResourcesRecovered), "save-failure-keeps-both-cancellation-results");
            Require(cancelled.All(value => value.Result!.Diagnostic!.Contains("予約ログの保存")),
                "both-cancellation-results-retain-save-failure");
            SchedulerRuntimeHost.Execution failed = badAccess.Host.Executions.Single(value =>
                value.Dispatch.Request.Entry.Id == failure.Id);
            Require(failed.Result!.Outcome == SchedulerExecutionOutcome.Failed
                && failed.Result.Diagnostic!.Contains("予約ログの保存")
                && failed.Result.ResourcesRecovered, "script-failure-retains-log-diagnostic");
            main.Close();
            bad.Close();
            await WaitForRuntimeHost(() => mainAccess.IsClosed && badAccess.IsClosed);
            Require(host.CanClose && badAccess.Host.CanClose, "all-log-executions-recovered");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                executions = 5,
                checks
            }));
            return 0;

            IEnumerable<string> LogPaths() => host.Executions.Select(value =>
                new SchedulerManagedStorage(logs).LogPath(value.Dispatch.Request.OccurrenceId));
        }
        finally
        {
            if (main.IsVisible)
            {
                main.Close();
            }
            if (bad.IsVisible)
            {
                bad.Close();
            }
        }

        bool Ready(string name) => File.Exists(Path.Combine(directory, name + ".ready"));

        SchedulerEntry Entry(string name, SchedulerExecutionMode mode, bool large = false)
        {
            string path = Path.Combine(directory, name + ".uws");
            string source = (large ? "DIM text=\"" + new string('界', 20_000)
                + "\"\nFOR n=1 TO 100\nPRINT text\nNEXT\n" : "PRINT \"one\"\n")
                + $"DIM f=FOPEN(\"{name}.ready\",F_WRITE8)\nFPUT(f,1)\nFCLOSE(f)\n"
                + $"WHILE !FOPEN(\"{name}.go\",F_EXISTS)\nSLEEP(0.02)\nWEND\nPRINT \"last\"";
            ScriptCompiler.Compile(source);
            WriteRuntimeHostScript(path, source);
            return new SchedulerEntry(Guid.NewGuid(), name, path, [], mode,
                new SchedulerStartupCondition(), directory);
        }

        async Task Initialize(MainWindow window, string name, string logDirectory, SchedulerEntry[] entries)
        {
            string settings = Path.Combine(directory, name + ".json");
            SchedulerSettingsStore.Save(settings, new SchedulerSettings(entries, enabled: true), null);
            window.Show();
            await window.RefreshInputCleanupAsync();
            await window.InitializeSchedulerAsync(settings, Path.Combine(directory, name + ".monitor"), logDirectory);
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }
}
