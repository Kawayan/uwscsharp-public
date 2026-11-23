using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerIdentityUiVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        string executable = File.ReadAllText(Path.Combine(directory, "probe-path.txt"));
        var checks = new List<string>();
        var errors = new List<Exception>();
        var windows = new List<MainWindow>();
        foreach (string mode in new[] { "foreign-control", "wrong-ready", "foreign-keyboard" })
        {
            string caseDirectory = Path.Combine(directory, mode);
            Directory.CreateDirectory(caseDirectory);
            string settings = Path.Combine(caseDirectory, "schedule.json");
            string logs = Path.Combine(caseDirectory, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "identity-probe"), mode);
            var clock = new IdentityVerificationClock();
            DateTimeOffset due = clock.Now.AddMinutes(1);
            string script = Path.Combine(caseDirectory, "never.uws");
            File.WriteAllText(script, "PRINT \"must-not-run\"", new UTF8Encoding(true));
            var entry = new SchedulerEntry(Guid.NewGuid(), mode, script, [], SchedulerExecutionMode.SeparateProcess,
                new SchedulerOneShotCondition(DateOnly.FromDateTime(due.UtcDateTime),
                    TimeOnly.FromDateTime(due.UtcDateTime)), caseDirectory);
            SchedulerSettingsStore.Save(settings, new SchedulerSettings([entry], enabled: true), null);
            var main = new MainWindow(false, confirmDiscard: () => true, reportError: errors.Add,
                launcherSettingsPath: Path.Combine(caseDirectory, "launcher.json"))
            {
                ShowActivated = false
            };
            var access = new MainWindow.SchedulerVerificationAccess(main);
            windows.Add(main);
            main.Show();
            await main.RefreshInputCleanupAsync();
            access.SetDraft("認証拒否でも保持する原稿");
            await access.InitializeAsync(settings,
                Path.Combine(caseDirectory, "monitor"), logs, clock,
                (_, _, _) => Task.FromException<SchedulerExecutionResult>(
                    new NotSupportedException()), executable: executable);
            SchedulerRuntimeHost host = access.Host!;
            Require(host.OwnsMonitor && host.IsActive && host.ActiveCount == 0,
                mode + "-normal-window-initializes-monitor");
            clock.Now = due;
            host.Poll();
            await WaitForRuntimeHost(() => File.Exists(Path.Combine(logs, "child.json"))
                && (mode == "wrong-ready" || File.Exists(Path.Combine(logs, "peer.json"))));
            var held = new List<Process>();
            try
            {
                string[] roles = mode == "wrong-ready" ? ["child"] : ["child", "peer"];
                foreach (string role in roles)
                {
                    using JsonDocument data = await ReadIdentityReceipt(Path.Combine(logs, role + ".json"));
                    var process = Process.GetProcessById(data.RootElement.GetProperty("pid").GetInt32());
                    _ = process.Handle;
                    held.Add(process);
                }
                Require(held.All(process => !process.HasExited)
                    && held.All(process => host.OwnedProcessIds.Contains((uint)process.Id)),
                    mode + "-owned-peers-live-before-connection");
                File.WriteAllText(Path.Combine(logs, "connect"), "go");
                await WaitForRuntimeHost(() => host.Executions.Count == 1
                    && host.Executions[0].Finished.Task.IsCompleted);
                SchedulerRuntimeHost.Execution execution = host.Executions[0];
                string expected = mode switch
                {
                    "foreign-control" => "予約workerの接続元PIDが一致しません。",
                    "wrong-ready" => "予約workerの準備通知が順序またはPIDと一致しません。",
                    _ => "キー送信の接続元PIDが一致しません。"
                };
                Require(execution.Result is
                {
                    Outcome: SchedulerExecutionOutcome.Unknown,
                    Started: false, ResourcesRecovered: false, Forced: true
                }
                    && execution.Result.Diagnostic?.Contains(expected, StringComparison.Ordinal) == true
                    && execution.ResultSaved && held.All(process => process.HasExited),
                    mode + "-runtime-persists-prestart-unknown-after-rejection");
                SchedulerOneShotState state = SchedulerSettingsStore.Load(settings).Settings.OneShotStates.Single();
                Require(state.Status == SchedulerOneShotStatus.Unknown
                    && state.OccurrenceId == execution.Dispatch.Request.OccurrenceId
                    && host.ActiveCount == 1 && access.ScheduledCount == 1
                    && !access.CanEnterInteractive && host.OwnedProcessIds.Count == 0,
                    mode + "-consumed-one-shot-and-unrecovered-gate-retained");
                main.Close();
                await WaitForRuntimeHost(() => access.IsExitHeld && !access.IsClosing);
                Require(main.IsVisible && !host.CanClose && host.OwnsMonitor
                    && !access.CanToggleMonitor
                    && access.StatusText.Contains("回収は未確認", StringComparison.Ordinal)
                    && access.DraftText == "認証拒否でも保持する原稿" && access.IsModified,
                    mode + "-normal-close-holds-window-and-shows-unconfirmed-recovery");
                InvokeRegistrationButton(main, "SchedulerSettingsButton");
                await WaitForRuntimeHost(() => access.SettingsMenu?.IsOpen == true);
                ContextMenu menu = access.SettingsMenu;
                MenuItem historyMenu = menu.Items.OfType<MenuItem>().Single(value => Equals(value.Header, "実行履歴…"));
                menu.IsOpen = false;
                historyMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await WaitForRuntimeHost(() => Application.Current.Windows.OfType<SchedulerHistoryWindow>().Any());
                SchedulerHistoryWindow history = Application.Current.Windows.OfType<SchedulerHistoryWindow>().Single();
                ListBox rows = FindRegistrationControl<ListBox>(history, "HistoryRows");
                TextBox detail = FindRegistrationControl<TextBox>(history, "HistoryDetails");
                await WaitForRuntimeHost(() => rows.Items.Count == 1);
                rows.SelectedIndex = 0;
                await WaitForRuntimeHost(() => detail.Text.Contains("結果: 結果不明"));
                var saved = (SchedulerHistorySummary)((ListBoxItem)rows.Items[0]).Tag;
                Require(saved.OccurrenceId == execution.Dispatch.Request.OccurrenceId
                    && detail.Text.Contains("開始情報: 未確認") && detail.Text.Contains("開始: 未確認")
                    && detail.Text.Contains("資源回収: 未確認") && detail.Text.Contains(expected)
                    && !detail.Text.Contains("結果: 成功") && !detail.Text.Contains("資源回収: 確認済み")
                    && history.IsVisible, mode + "-history-ui-shows-unknown-not-started-and-unconfirmed");
                history.Close();
                host.Poll();
                Require(SchedulerSettingsStore.Load(settings).Settings.OneShotStates.Single().Status
                    == SchedulerOneShotStatus.Unknown && host.Executions.Count == 1
                    && host.ActiveCount == 1 && !File.Exists(Path.Combine(logs, "start.bad")),
                    mode + "-viewing-history-does-not-replay-or-clear-unknown");
            }
            finally
            {
                await host.StopAsync(TimeSpan.FromSeconds(20));
                foreach (Process process in held)
                {
                    process.Dispose();
                }
            }
        }
        Require(errors.Count == 0 && windows.All(window => window.IsVisible), "all-authentication-fault-windows-retain-drafts");
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            result = "Passed",
            process_id = Environment.ProcessId,
            checks
        }));
        // 未回収枠を偽って解放せず、専用診断の終了で残るOS資源を閉じます。
        return 0;

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }

    private static async Task<JsonDocument> ReadIdentityReceipt(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await JsonDocument.ParseAsync(stream);
    }

    private sealed class IdentityVerificationClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
