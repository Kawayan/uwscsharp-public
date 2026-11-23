using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static UWSCSharp.Wpf.SchedulerVerification;

namespace UWSCSharp.Wpf;

internal static class SchedulerRegistrationVerification
{
    internal static async Task<int> RunAsync(
        string directory, string receipt)
    {
        Directory.CreateDirectory(directory);
        string settings = Path.Combine(directory, "schedule.json");
        var checks = new List<string>();
        var observations = new Dictionary<string, bool>();
        var main = new MainWindow(false, confirmDiscard: () => true) { ShowActivated = false };
        var access = new MainWindow.SchedulerVerificationAccess(main);
        string profile = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "UWSC", "UWSC.INI");
        byte[]? previousProfile = File.Exists(profile) ? await File.ReadAllBytesAsync(profile) : null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            await File.WriteAllTextAsync(profile, "[SCHEDULE]\nMaxNumber=80\n");
            main.Show();
            await main.RefreshInputCleanupAsync();
            access.SetDraft("登録画面で変えない未保存原稿");
            await main.InitializeSchedulerAsync(settings, Path.Combine(directory, "schedule.monitor"), Path.Combine(directory, "logs"));
            Task editing = main.Dispatcher.InvokeAsync(access.EditSettings).Task;
            SchedulerSettingsWindow dialog = await SettingsDialog();
            nint ownerHandle = new WindowInteropHelper(main).Handle;
            observations["dialog_visible"] = dialog.IsVisible;
            observations["owner_handle_created"] = ownerHandle != 0;
            observations["owner_wpf_enabled"] = main.IsEnabled;
            observations["owner_native_enabled"] = UWSCSharp.Windows.Native.IsWindowEnabled(ownerHandle);
            observations["settings_exists"] = File.Exists(settings);
            Console.WriteLine(JsonSerializer.Serialize(observations));
            AssertObserved(observations["dialog_visible"], "normal-ui-dialog-visible");
            AssertObserved(observations["owner_handle_created"]
                && !observations["owner_native_enabled"], "normal-ui-owner-native-disabled");
            AssertObserved(!observations["settings_exists"], "normal-ui-draft-not-written");
            checks.Add("normal-ui-owner-disabled-and-draft-visible");
            Require(dialog.Draft.Settings.Slots.Count == 80
                && FindRegistrationControl<TextBox>(dialog, "ScheduleCapacity").Text == "80",
                "missing-json-initial-capacity-from-ini");
            for (int kind = 0; kind < 6; kind++)
            {
                FindRegistrationControl<ListBox>(dialog, "ScheduleSlots").SelectedIndex = kind;
                InvokeRegistrationButton(dialog, "ScheduleEdit");
                await WaitForRuntimeHost(() => Application.Current.Windows.OfType<SchedulerEntryWindow>().Any());
                SchedulerEntryWindow entry = Application.Current.Windows.OfType<SchedulerEntryWindow>().Single();
                FindRegistrationControl<TextBox>(entry, "ScheduleEntryTitle").Text = "条件" + kind;
                FindRegistrationControl<TextBox>(entry, "ScheduleEntryPath").Text = "jobs\\sample.uws";
                FindRegistrationControl<ComboBox>(entry, "ScheduleEntryMode").SelectedIndex = kind % 2;
                FindRegistrationControl<ComboBox>(entry, "ScheduleEntryCondition").SelectedIndex = kind;
                FindRegistrationControl<TextBox>(entry, "ScheduleIntervalValue").Text = "30";
                FindRegistrationControl<TextBox>(entry, "ScheduleTime").Text = "09:15";
                FindRegistrationControl<ComboBox>(entry, "ScheduleDayKind").SelectedIndex = 1;
                FindRegistrationControl<TextBox>(entry, "ScheduleDays").Text = "1,3";
                FindRegistrationControl<TextBox>(entry, "ScheduleExpression").Text = "NN=0";
                FindRegistrationControl<TextBox>(entry, "ScheduleDate").Text = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                FindRegistrationControl<TextBox>(entry, "ScheduleOnceTime").Text = "10:30";
                FindRegistrationControl<TextBox>(entry, "ScheduleWindowTitle").Text = "保存だけの窓";
                if (kind == 0)
                {
                    FindRegistrationControl<TextBox>(entry, "ScheduleEntryPath").Text = "invalid.txt";
                    InvokeRegistrationButton(entry, "ScheduleEntryApply");
                    await WaitForRuntimeHost(() => FindRegistrationControl<TextBlock>(entry, "ScheduleEntryError").Text.Length > 0);
                    Require(entry.IsVisible && entry.Result is null, "entry-invalid-extension-keeps-dialog");
                    FindRegistrationControl<TextBox>(entry, "ScheduleEntryPath").Text = "jobs\\sample.uws";
                    entry.UpdateLayout();
                    Button apply = FindRegistrationControl<Button>(entry, "ScheduleEntryApply");
                    Point location = apply.TransformToAncestor(entry).Transform(new Point());
                    Require(entry.ActualHeight <= SystemParameters.WorkArea.Height
                        && location.Y >= 0 && location.Y + apply.ActualHeight <= entry.ActualHeight,
                        "entry-footer-within-workarea");
                    foreach (string value in new[] { "", "a b", "日本語" })
                    {
                        FindRegistrationControl<TextBox>(entry, "ScheduleArgumentValue").Text = value;
                        int count = FindRegistrationControl<ListBox>(entry, "ScheduleArguments").Items.Count;
                        InvokeRegistrationButton(entry, "ScheduleArgumentAdd");
                        await WaitForRuntimeHost(() => FindRegistrationControl<ListBox>(entry, "ScheduleArguments").Items.Count == count + 1);
                    }
                }
                InvokeRegistrationButton(entry, "ScheduleEntryApply");
                await WaitForRuntimeHost(() => !entry.IsVisible);
            }
            Require(dialog.Draft.Settings.Slots.Count(value => value is not null) == 6, "all-six-condition-editors-reflected-in-draft");
            Require(dialog.Draft.Settings.Slots[0]!.Arguments.SequenceEqual(["", "a b", "日本語"]), "ordered-arguments-and-empty-value-retained");
            FindRegistrationControl<ListBox>(dialog, "ScheduleSlots").SelectedIndex = 0;
            InvokeRegistrationButton(dialog, "ScheduleCopy");
            await WaitForRuntimeHost(() => dialog.Draft.Settings.Slots[6] is not null);
            Guid copiedId = dialog.Draft.Settings.Slots[6]!.Id;
            Require(copiedId != dialog.Draft.Settings.Slots[0]!.Id, "copy-uses-new-identity");
            InvokeRegistrationButton(dialog, "ScheduleUp");
            await WaitForRuntimeHost(() => dialog.Draft.Settings.Slots[5]?.Id == copiedId);
            Require(dialog.Draft.Settings.Slots[6]!.Condition is SchedulerWindowCondition, "move-preserves-identity-and-shifts-existing-row");
            InvokeRegistrationButton(dialog, "ScheduleDelete");
            await WaitForRuntimeHost(() => dialog.Draft.Settings.Slots[5] is null);
            FindRegistrationControl<TextBox>(dialog, "ScheduleCapacity").Text = "41";
            InvokeRegistrationButton(dialog, "ScheduleResize");
            await WaitForRuntimeHost(() => dialog.Draft.Settings.Slots.Count == 41);
            FindRegistrationControl<CheckBox>(dialog, "ScheduleMonitorEnabled").IsChecked = true;
            Require(!File.Exists(settings) && access.Settings!.Current!.Settings.Slots.All(value => value is null), "draft-not-written-before-save");
            InvokeRegistrationButton(dialog, "ScheduleSettingsSave");
            await editing.WaitAsync(TimeSpan.FromSeconds(10));
            SchedulerSettings saved = SchedulerSettingsStore.Load(settings).Settings;
            Require(saved.Enabled && !access.Host!.IsActive && access.Host.ActiveCount == 0, "enabled-setting-does-not-autostart-stopped-monitor");
            Require(saved.Slots.Count == 41 && saved.Slots[0]!.Condition is SchedulerStartupCondition
                && saved.Slots[1]!.Condition is SchedulerIntervalCondition { Value: 30 }
                && saved.Slots[2]!.Condition is SchedulerTimeCondition { DayKind: SchedulerDayKind.Weekdays }
                && saved.Slots[3]!.Condition is SchedulerExpressionCondition { Source: "NN=0" }
                && saved.Slots[4]!.Condition is SchedulerOneShotCondition
                && saved.Slots[6]!.Condition is SchedulerWindowCondition
                && saved.Slots[0]!.Arguments.SequenceEqual(["", "a b", "日本語"])
                && Path.IsPathFullyQualified(saved.Slots[0]!.Path), "all-six-typed-conditions-persisted");

            await File.WriteAllTextAsync(profile, "[SCHEDULE]\nMaxNumber=200\n");
            editing = main.Dispatcher.InvokeAsync(access.EditSettings).Task;
            dialog = await SettingsDialog();
            Require(dialog.Draft.Settings.Slots.Count == 41
                && await File.ReadAllTextAsync(profile) == "[SCHEDULE]\nMaxNumber=200\n",
                "saved-json-capacity-wins-without-changing-ini");
            SchedulerEntry first = dialog.Draft.Settings.Slots[0]!;
            dialog.Draft.SetEntry(0, new SchedulerEntry(first.Id, "競合でも残す原稿", first.Path,
                first.Arguments, first.Mode, first.Condition, directory));
            access.Settings!.Save(access.Settings.Current!.Settings.WithOptions(false, false, false));
            byte[] concurrent = File.ReadAllBytes(settings);
            InvokeRegistrationButton(dialog, "ScheduleSettingsSave");
            await WaitForRuntimeHost(() => FindRegistrationControl<TextBlock>(dialog, "ScheduleSettingsError").Text.Length > 0);
            Require(dialog.IsVisible && dialog.Draft.Settings.Slots[0]!.Title == "競合でも残す原稿", "stale-save-keeps-ui-and-draft");
            dialog.Close();
            await editing.WaitAsync(TimeSpan.FromSeconds(10));
            Require(File.ReadAllBytes(settings).SequenceEqual(concurrent), "cancel-preserves-concurrent-save");

            var once = (SchedulerOneShotCondition)access.Settings.Current!.Settings.Slots[4]!.Condition;
            Guid onceId = access.Settings.Current.Settings.Slots[4]!.Id;
            access.Settings.Save(access.Settings.Current.Settings.WithOneShotStates([
                new SchedulerOneShotState(onceId, once.Date, once.Time, Guid.NewGuid(), DateTimeOffset.UtcNow, SchedulerOneShotStatus.Succeeded)]));
            editing = main.Dispatcher.InvokeAsync(access.EditSettings).Task;
            dialog = await SettingsDialog();
            FindRegistrationControl<ListBox>(dialog, "ScheduleSlots").SelectedIndex = 4;
            byte[] beforeRearm = File.ReadAllBytes(settings);
            InvokeRegistrationButton(dialog, "ScheduleRearm");
            await WaitForRuntimeHost(() => dialog.Draft.IsRearmed(onceId));
            Require(File.ReadAllBytes(settings).SequenceEqual(beforeRearm), "rearm-is-staged-before-save");
            InvokeRegistrationButton(dialog, "ScheduleSettingsSave");
            await editing.WaitAsync(TimeSpan.FromSeconds(10));
            Require(access.Settings.Current!.Settings.OneShotStates.Count == 0
                && access.Settings.Current.Settings.Slots[4]!.Id == onceId, "explicit-future-rearm-clears-only-consumption");
            Require(access.DraftText == "登録画面で変えない未保存原稿" && access.IsModified, "script-editor-remains-unchanged");

            File.WriteAllText(settings, "{");
            access.ReloadSettings();
            InvokeRegistrationButton(main, "SchedulerSettingsButton");
            await WaitForRuntimeHost(() => access.SettingsMenu?.IsOpen == true);
            MenuItem[] menu = [.. access.SettingsMenu.Items.OfType<MenuItem>()];
            Require(menu.Length == 4 && !menu[0].IsEnabled && menu[1].IsEnabled && menu[2].IsEnabled && menu[3].IsEnabled
                && File.ReadAllText(settings) == "{", "corrupt-settings-offer-explicit-recovery-without-overwrite");
            access.SettingsMenu.IsOpen = false;
            main.Close();
            await WaitForRuntimeHost(() => access.IsClosed);
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                process_id = Environment.ProcessId,
                condition_editors = 6,
                observations,
                checks
            }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                result = "Failed",
                process_id = Environment.ProcessId,
                observations,
                checks,
                error = error.ToString()
            }));
            throw;
        }
        finally
        {
            try
            {
                foreach (Window window in Application.Current.Windows.Cast<Window>().Reverse().ToArray())
                {
                    window.Close();
                }
            }
            finally
            {
                if (previousProfile is null)
                {
                    File.Delete(profile);
                }
                else
                {
                    await File.WriteAllBytesAsync(profile, previousProfile);
                }
            }
        }

        async Task<SchedulerSettingsWindow> SettingsDialog()
        {
            await WaitForRuntimeHost(() => Application.Current.Windows.OfType<SchedulerSettingsWindow>().Any());
            return Application.Current.Windows.OfType<SchedulerSettingsWindow>().Single();
        }

        void Require(bool condition, string name)
        {
            AssertObserved(condition, name);
            checks.Add(name);
        }

        void AssertObserved(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(
                    name + ": " + JsonSerializer.Serialize(observations));
            }
        }
    }

}
