using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerHotkeyRestoreVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        const int key = 122;
        string logs = Path.Combine(directory, "logs");
        string settings = Path.Combine(directory, "schedule.json");
        string contention = Path.Combine(directory, "contention");
        Directory.CreateDirectory(contention);
        var start = new ProcessStartInfo(File.ReadAllText(Path.Combine(directory, "probe-path.txt")))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--hotkey-reservation-probe");
        start.ArgumentList.Add(contention);
        start.ArgumentList.Add(key.ToString(CultureInfo.InvariantCulture));
        using Process competitor = Process.Start(start) ?? throw new InvalidOperationException("競合元を開始できません。");
        Task<string> competitorErrors = competitor.StandardError.ReadToEndAsync();
        var checks = new List<string>();
        var checkObservations = new List<HotkeyRestoreCheckObservation>();
        var errors = new List<Exception>();
        var main = new MainWindow(false, confirmDiscard: () => true, reportError: errors.Add,
            launcherSettingsPath: Path.Combine(directory, "launcher.json"))
        {
            ShowActivated = false
        };
        var access = new MainWindow.SchedulerVerificationAccess(main);
        var clock = new HotkeyRestoreClock();
        int down = 0;
        int up = 0;
        int scopes = 0;
        string? keyboardFailure = null;
        long keyboardStarted = 0;
        long keyboardEnded = 0;
        main.Editor.PreviewKeyDown += (_, args) =>
        {
            if (KeyInterop.VirtualKeyFromKey(args.Key) == key)
            {
                down++;
                args.Handled = true;
            }
        };
        main.Editor.PreviewKeyUp += (_, args) =>
        {
            if (KeyInterop.VirtualKeyFromKey(args.Key) == key)
            {
                up++;
                args.Handled = true;
            }
        };
        try
        {
            main.Show();
            await main.RefreshInputCleanupAsync();
            access.SetParentHotkeySettings(new HotkeySettings(Modifiers: 0,
                RecordKey: 119, PauseKey: 120, StopKey: 121, PlayKey: key));
            Require(access.ReapplyParentHotkeys() && access.IsParentHotkeyRegistered(4),
                "parent-native-hotkey-registered-before-worker-input");
            nint target = new WindowInteropHelper(main).Handle;
            string script = Path.Combine(directory, "worker.uws");
            File.WriteAllText(script, "DIM f=FOPEN(\"worker.ready\",F_WRITE8)\nFCLOSE(f)\n"
                + "WHILE !FOPEN(\"worker.go\",F_EXISTS)\nSLEEP(0.02)\nWEND\n"
                + "KBD(122,CLICK)\nf=FOPEN(\"after-key.bad\",F_WRITE8)\nFCLOSE(f)\n", new UTF8Encoding(true));
            DateTimeOffset due = clock.Now.AddMinutes(1);
            var entry = new SchedulerEntry(Guid.NewGuid(), "restore-failure", script, [],
                SchedulerExecutionMode.SeparateProcess,
                new SchedulerOneShotCondition(DateOnly.FromDateTime(due.UtcDateTime),
                    TimeOnly.FromDateTime(due.UtcDateTime)), directory);
            SchedulerSettingsStore.Save(settings, new SchedulerSettings([entry], enabled: true), null);
            await access.InitializeAsync(settings,
                Path.Combine(directory, "monitor"), logs, clock,
                (_, _, _) => Task.FromException<SchedulerExecutionResult>(
                    new NotSupportedException()),
                confirmUnrecoveredExit: () => false,
                keyboardGuard: (pressed, operation, token) =>
                    ObserveKeyboardAsync(() => access.RunWorkerKeyboardAsync(pressed, async () =>
                    {
                        scopes++;
                        Require(pressed == key && !access.IsParentHotkeyRegistered(4)
                            && UWSCSharp.Windows.Native.GetForegroundWindow() == target
                            && main.Editor.IsKeyboardFocused,
                            "owned-input-target-and-parent-suspension-confirmed");
                        await operation();
                        // CLICKのUPは親の復元応答後なので、ここではDOWNだけを待ちます。
                        await WaitForRuntimeHost(() => down == 1);
                        File.WriteAllText(Path.Combine(contention, "acquire"), "go");
                        await WaitForRuntimeHost(() => competitor.HasExited
                            || File.Exists(Path.Combine(contention, "registered.json")));
                        if (competitor.HasExited)
                        {
                            throw new InvalidOperationException("競合元が登録前に終了しました。" + await competitorErrors);
                        }
                        using JsonDocument data = await ReadHotkeyRestoreReceipt(Path.Combine(contention, "registered.json"));
                        Require(data.RootElement.GetProperty("pid").GetInt32() == competitor.Id
                            && data.RootElement.GetProperty("key").GetInt32() == key,
                            "other-process-holds-same-native-hotkey-before-parent-restore");
                    }, token)));
            SchedulerRuntimeHost host = access.Host!;
            clock.Now = due;
            host.Poll();
            await WaitForRuntimeHost(() => File.Exists(Path.Combine(directory, "worker.ready")));
            main.EditorPanel.IsExpanded = true;
            main.UpdateLayout();
            main.Activate();
            main.Editor.Focus();
            Require(UWSCSharp.Windows.Native.GetForegroundWindow() == target && main.Editor.IsKeyboardFocused
                && main.WindowState != WindowState.Minimized, "guest-owned-focus-established-before-key-input");
            File.WriteAllText(Path.Combine(directory, "worker.go"), "go");
            await WaitForRuntimeHost(() => host.Executions.Single().Finished.Task.IsCompleted);
            SchedulerRuntimeHost.Execution execution = host.Executions.Single();
            // 復元失敗後の入力回収によるUPも、所有する編集欄で確認します。
            await WaitForRuntimeHost(() => up == 1);
            Require(down == 1 && UWSCSharp.Windows.Native.GetForegroundWindow() == target
                && main.Editor.IsKeyboardFocused, "actual-key-down-up-received-by-owned-editor");
            Require(scopes == 1 && down == 1 && up == 1 && execution.Result is
            { Outcome: SchedulerExecutionOutcome.Failed, Started: true, ResourcesRecovered: false, ExitCode: 1 }
                && execution.Result.Diagnostic?.Contains("ホットキー再登録に失敗", StringComparison.Ordinal) == true
                && execution.ResultSaved && !File.Exists(Path.Combine(directory, "after-key.bad")),
                "restore-failure-is-script-failure-with-unconfirmed-resources");
            Require(!access.IsParentHotkeyRegistered(4) && access.IsParentHotkeyRegistered(1)
                && access.IsParentHotkeyRegistered(2) && access.IsParentHotkeyRegistered(3)
                && main.HotkeyLabel.Text.Contains("失敗", StringComparison.Ordinal),
                "parent-reports-failed-restore-and-keeps-unaffected-registrations");
            File.WriteAllText(Path.Combine(contention, "release"), "go");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await competitor.WaitForExitAsync(timeout.Token);
            Require(competitor.ExitCode == 0 && access.ReapplyParentHotkeys()
                && access.IsParentHotkeyRegistered(4), "native-hotkey-can-be-reregistered-after-conflict-release");
            Require(host.ActiveCount == 1 && access.ScheduledCount == 1
                && !access.CanEnterInteractive && !host.CanClose
                && host.OwnedProcessIds.Count == 0,
                "reregistration-does-not-falsely-clear-execution-recovery-state");
            main.Close();
            await WaitForRuntimeHost(() => access.IsExitHeld && !access.IsClosing);
            SchedulerHistoryRecord saved = access.ReadHistoryRecord(execution.Dispatch.Request.OccurrenceId);
            Require(main.IsVisible && saved.Outcome == SchedulerExecutionOutcome.Failed
                && saved.Started && !saved.ResourcesRecovered && saved.IsProtected
                && SchedulerSettingsStore.Load(settings).Settings.OneShotStates.Single().Status
                    == SchedulerOneShotStatus.Failed && errors.Count == 0,
                "failed-one-shot-and-protected-history-retained-with-close-hold");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                competitor_pid = competitor.Id,
                key,
                down,
                up,
                scopes,
                checks,
                check_observations = checkObservations,
                timestamp_frequency = Stopwatch.Frequency
            }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(receipt + ".conditions", JsonSerializer.Serialize(new
            {
                down,
                up,
                scopes,
                foreground = UWSCSharp.Windows.Native.GetForegroundWindow().ToInt64(),
                owned_window = new WindowInteropHelper(main).Handle.ToInt64(),
                editor_focused = main.Editor.IsKeyboardFocused,
                window_state = main.WindowState.ToString(),
                parent_registered = access.TryReadParentHotkeyRegistration(4),
                competitor_exited = competitor.HasExited,
                completed_checks = checks,
                check_observations = checkObservations,
                keyboard_failure = keyboardFailure,
                keyboard_started = keyboardStarted,
                keyboard_ended = keyboardEnded,
                timestamp_frequency = Stopwatch.Frequency,
                after_key_exists = File.Exists(Path.Combine(directory, "after-key.bad")),
                executions = access.Host?.Executions.Select(execution => new
                {
                    finished = execution.Finished.Task.IsCompleted,
                    parent_cancellation = execution.Cancellation.IsCancellationRequested,
                    saved = execution.ResultSaved,
                    outcome = execution.Result?.Outcome.ToString(),
                    started = execution.Result?.Started,
                    exit_code = execution.Result?.ExitCode,
                    resources_recovered = execution.Result?.ResourcesRecovered,
                    forced = execution.Result?.Forced,
                    diagnostic = execution.Result?.Diagnostic
                }).ToArray(),
                error = error.ToString()
            }));
            throw;
        }
        finally
        {
            File.WriteAllText(Path.Combine(contention, "release"), "go");
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await competitor.WaitForExitAsync(cleanup.Token);
            }
            catch (OperationCanceledException)
            {
                competitor.Kill(entireProcessTree: true);
                await competitor.WaitForExitAsync();
            }
            await competitorErrors;
            if (access.Host is not null)
            {
                await access.Host.StopAsync(TimeSpan.FromSeconds(20));
            }
        }

        async Task ObserveKeyboardAsync(Func<Task> operation)
        {
            keyboardStarted = Stopwatch.GetTimestamp();
            try
            {
                await operation();
            }
            catch (Exception error)
            {
                keyboardFailure = error.ToString();
                throw;
            }
            finally
            {
                keyboardEnded = Stopwatch.GetTimestamp();
            }
        }

        void Require(bool condition, string name)
        {
            checkObservations.Add(new HotkeyRestoreCheckObservation(
                name, condition, Stopwatch.GetTimestamp()));
            VerificationAssert.Require(checks, condition, name);
        }
    }

    // 条件の成否と単調増加時計を残し、通信期限との前後関係を調べます。
    private sealed record HotkeyRestoreCheckObservation(
        string Name, bool Passed, long Timestamp);

    private static async Task<JsonDocument> ReadHotkeyRestoreReceipt(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await JsonDocument.ParseAsync(stream);
    }

    private sealed class HotkeyRestoreClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
