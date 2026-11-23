using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerHistoryVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string logs = Path.Combine(directory, "logs");
        string histories = Path.Combine(logs, "managed", "history");
        var store = new SchedulerHistoryStore(histories);
        var accepted = new DateTimeOffset(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);
        string fullTitle = new string('題', 280) + "全文の末尾";
        for (int index = 1; index <= 51; index++)
        {
            var entry = new SchedulerEntry(Guid.NewGuid(), index == 51 ? fullTitle : "保存題名" + index,
                Path.Combine(directory, "old.uws"), ["", "a b", "日本語\n次行"],
                SchedulerExecutionMode.SeparateProcess, index == 51
                    ? new SchedulerWindowCondition("保存窓", "Main") : new SchedulerStartupCondition(), directory);
            var record = SchedulerHistoryRecord.Capture(new SchedulerDispatch(Guid.NewGuid(),
                new SchedulerExecutionRequest(Guid.ParseExact(index.ToString("x32"), "N"), entry, accepted,
                    index == 51 ? new(new(0x20000000000001, 123, 456), accepted) : null)));
            if (index == 51)
            {
                record = record.MarkStarted(accepted.AddSeconds(1)).Complete(
                    SchedulerExecutionResult.Unknown(true, null, false, "保存した旧診断"),
                    accepted.AddSeconds(2), endedConfirmed: false);
            }
            store.Save(record);
            if (index == 50)
            {
                string path = Path.Combine(histories, record.OccurrenceId.ToString("N") + ".json");
                JsonObject legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                legacy["version"] = 2;
                legacy.Remove("window");
                File.WriteAllText(path, legacy.ToJsonString());
            }
        }
        Dictionary<string, byte[]> hashes = Directory.GetFiles(histories, "*.json")
            .ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        var checks = new List<string>();
        var main = new MainWindow(false, confirmDiscard: () => true) { ShowActivated = false };
        var access = new MainWindow.SchedulerVerificationAccess(main);
        try
        {
            main.Show();
            await main.RefreshInputCleanupAsync();
            access.SetDraft("履歴画面で変えない原稿");
            await main.InitializeSchedulerAsync(Path.Combine(directory, "schedule.json"),
                Path.Combine(directory, "schedule.monitor"), logs);
            InvokeRegistrationButton(main, "SchedulerSettingsButton");
            await WaitForRuntimeHost(() => access.SettingsMenu?.IsOpen == true);
            ContextMenu menu = access.SettingsMenu;
            MenuItem historyMenu = menu.Items.OfType<MenuItem>().Single(value => Equals(value.Header, "実行履歴…"));
            menu.IsOpen = false;
            Task viewing = main.Dispatcher.InvokeAsync(() => historyMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent))).Task;
            await WaitForRuntimeHost(() => Application.Current.Windows.OfType<SchedulerHistoryWindow>().Any());
            SchedulerHistoryWindow window = Application.Current.Windows.OfType<SchedulerHistoryWindow>().Single();
            ListBox rows = FindRegistrationControl<ListBox>(window, "HistoryRows");
            TextBox detail = FindRegistrationControl<TextBox>(window, "HistoryDetails");
            TextBlock status = FindRegistrationControl<TextBlock>(window, "HistoryStatus");
            await WaitForRuntimeHost(() => rows.Items.Count == 50);
            Require(window.IsVisible && main.IsEnabled && detail.IsReadOnly, "menu-opens-readonly-history-with-main-controls-enabled");
            var newest = Guid.ParseExact(51.ToString("x32"), "N");
            Require(((SchedulerHistorySummary)((ListBoxItem)rows.Items[0]).Tag).OccurrenceId == newest,
                "first-page-has-fifty-in-stable-receipt-order");
            rows.SelectedIndex = 0;
            await WaitForRuntimeHost(() => detail.Text.Contains(fullTitle));
            Require(detail.Text.Contains("1: \"\"") && detail.Text.Contains("3: \"日本語\\n次行\"")
                && detail.Text.Contains("保存した旧診断"), "full-snapshot-arguments-and-diagnostic-visible");
            Require(detail.Text.Contains("終了確認: 未確認") && detail.Text.Contains("資源回収: 未確認")
                && detail.Text.Contains("結果: 結果不明"), "unknown-history-is-not-rewritten-as-completed");
            bool captured = detail.Text.Contains("HWND値: 9007199254740993")
                && detail.Text.Contains("PID: 123 / TID: 456")
                && detail.Text.Contains("現在の有効性は未確認");
            rows.SelectedIndex = 1;
            await WaitForRuntimeHost(() => detail.Text.Contains("題名: 保存題名50"));
            Require(captured && detail.Text.Contains("捕捉先: 未記録"),
                "captured-window-and-legacy-absence-are-saved-observations");
            InvokeRegistrationButton(window, "HistoryNext");
            await WaitForRuntimeHost(() => rows.Items.Count == 1);
            Require(((SchedulerHistorySummary)((ListBoxItem)rows.Items[0]).Tag).OccurrenceId
                == Guid.ParseExact(1.ToString("x32"), "N"), "next-page-shows-remaining-record");
            InvokeRegistrationButton(window, "HistoryBack");
            await WaitForRuntimeHost(() => rows.Items.Count == 50);
            Require(((SchedulerHistorySummary)((ListBoxItem)rows.Items[0]).Tag).OccurrenceId == newest,
                "previous-page-restores-first-page");
            rows.SelectedIndex = 0;
            await WaitForRuntimeHost(() => detail.Text.Contains(fullTitle));
            string before = detail.Text;
            string bad = Path.Combine(histories, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(bad, "{");
            InvokeRegistrationButton(window, "HistoryReload");
            await WaitForRuntimeHost(() => status.Text.Contains("履歴を読み込めません"));
            Require(rows.Items.Count == 50 && detail.Text == before && File.ReadAllText(bad) == "{",
                "failed-refresh-keeps-page-detail-and-original-file");
            File.Delete(bad);
            InvokeRegistrationButton(window, "HistoryReload");
            await WaitForRuntimeHost(() => status.Text.StartsWith("50件", StringComparison.Ordinal));
            Require(rows.Items.Count == 50 && detail.Text == "", "successful-refresh-clears-old-detail-and-error");
            rows.SelectedIndex = 0;
            await WaitForRuntimeHost(() => detail.Text.Contains(fullTitle));
            string removed = Path.Combine(histories, newest.ToString("N") + ".json");
            File.Delete(removed);
            rows.SelectedIndex = -1;
            rows.SelectedIndex = 0;
            await WaitForRuntimeHost(() => detail.Text.Contains("詳細を読み込めません"));
            Require(!detail.Text.Contains(fullTitle), "removed-record-does-not-display-previous-definition");
            Require(hashes.Where(value => value.Key != removed).All(value =>
                SHA256.HashData(File.ReadAllBytes(value.Key)).AsSpan().SequenceEqual(value.Value)),
                "reading-does-not-modify-remaining-history");
            Require(access.DraftText == "履歴画面で変えない原稿" && access.IsModified
                && !access.Host!.IsActive, "editor-and-stopped-monitor-remain-unchanged");
            window.Close();
            await viewing.WaitAsync(TimeSpan.FromSeconds(10));
            main.Close();
            await WaitForRuntimeHost(() => access.IsClosed);
            Require(access.Host!.CanClose, "history-window-closes-without-monitor-ownership");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                seeded_records = 51,
                checks
            }));
            return 0;
        }
        finally
        {
            foreach (Window window in Application.Current.Windows.Cast<Window>().Reverse().ToArray())
            {
                window.Close();
            }
        }

        void Require(bool condition, string name)
            => VerificationAssert.Require(checks, condition, name);
    }
}
