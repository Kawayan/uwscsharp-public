using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UWSCSharp.Wpf;

/// <summary>
/// 予約の登録原稿を表示し、保存成功まで原稿とエラーを保持します。
/// </summary>
internal sealed class SchedulerSettingsWindow : Window
{
    private readonly SchedulerRegistrationDraft draft;
    private readonly ListBox slots = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly string applicationDirectory;

    internal SchedulerRegistrationDraft Draft => draft;

    internal SchedulerSettingsWindow(SchedulerRegistrationDraft draft,
        string applicationDirectory, Action<SchedulerRegistrationDraft> save)
    {
        this.draft = draft;
        this.applicationDirectory = applicationDirectory;
        Title = "予約の登録";
        Width = 860;
        Height = 650;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        MinWidth = 600;
        MinHeight = Math.Min(480, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        top.Children.Add(new TextBlock
        {
            Text = "変更は「設定を保存」で確定します。編集中に実行状態が保存された場合も競合として拒否します。停止中の監視は保存だけでは開始しません。",
            TextWrapping = TextWrapping.Wrap
        });
        var enabled = new CheckBox { Content = "予約監視を有効にする", IsChecked = draft.Settings.Enabled, Margin = new Thickness(0, 8, 0, 4) };
        SchedulerEntryWindow.Identify(enabled, "ScheduleMonitorEnabled");
        enabled.Checked += (_, _) => draft.SetEnabled(true);
        enabled.Unchecked += (_, _) => draft.SetEnabled(false);
        top.Children.Add(enabled);
        var beep = new CheckBox { Content = "予約の実行開始時にビープを鳴らす", IsChecked = draft.Settings.Beep, Margin = new Thickness(0, 0, 0, 4) };
        SchedulerEntryWindow.Identify(beep, "ScheduleStartSoundEnabled");
        beep.Checked += (_, _) => draft.SetBeep(true);
        beep.Unchecked += (_, _) => draft.SetBeep(false);
        top.Children.Add(beep);
        var wakeUp = new CheckBox { Content = "時刻条件に合わせて休止から復帰する", IsChecked = draft.Settings.WakeUp, Margin = new Thickness(0, 0, 0, 4) };
        SchedulerEntryWindow.Identify(wakeUp, "ScheduleWakeEnabled");
        wakeUp.Checked += (_, _) => draft.SetWakeUp(true);
        wakeUp.Unchecked += (_, _) => draft.SetWakeUp(false);
        top.Children.Add(wakeUp);
        top.Children.Add(new TextBlock
        {
            Text = "自動復帰はOSと機器の設定に依存します。数値式などでは探索を続けるための復帰もあります。窓条件だけでは復帰時刻を作りません。",
            TextWrapping = TextWrapping.Wrap
        });
        var capacityRow = new WrapPanel();
        top.Children.Add(capacityRow);
        capacityRow.Children.Add(new TextBlock { Text = "枠数（40〜400）", VerticalAlignment = VerticalAlignment.Center });
        var capacity = new TextBox { Text = draft.Settings.Slots.Count.ToString(CultureInfo.InvariantCulture), Width = 70, Margin = new Thickness(8, 0, 4, 0) };
        SchedulerEntryWindow.Identify(capacity, "ScheduleCapacity");
        AutomationProperties.SetName(capacity, "予約の枠数");
        capacityRow.Children.Add(capacity);
        SchedulerEntryWindow.Button(capacityRow, "枠数を変更", "ScheduleResize", () => TryEdit(() =>
        {
            draft.Resize(int.Parse(capacity.Text, CultureInfo.InvariantCulture));
            Refresh(0);
        }));
        top.Children.Add(new TextBlock { Text = "ウィンドウ条件・ビープ・休止復帰の実行接続は未実装です。ビープと休止復帰の既存値は維持します。", TextWrapping = TextWrapping.Wrap });
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        SchedulerEntryWindow.Identify(error, "ScheduleSettingsError");
        footer.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(actions);
        Button accept = SchedulerEntryWindow.Button(actions, "設定を保存", "ScheduleSettingsSave", () => TryEdit(() =>
        {
            save(draft);
            DialogResult = true;
        }));
        accept.IsDefault = true;
        Button cancel = SchedulerEntryWindow.Button(actions, "取消", "ScheduleSettingsCancel", () => { });
        cancel.IsCancel = true;
        var edits = new StackPanel { Margin = new Thickness(12, 8, 0, 0) };
        DockPanel.SetDock(edits, Dock.Right);
        root.Children.Add(edits);
        SchedulerEntryWindow.Button(edits, "登録・編集…", "ScheduleEdit", Edit);
        SchedulerEntryWindow.Button(edits, "空き枠へコピー", "ScheduleCopy", () => TryEdit(() =>
        {
            int target = Enumerable.Range(0, draft.Settings.Slots.Count)
                .FirstOrDefault(index => draft.Settings.Slots[index] is null, -1);
            if (target < 0)
            {
                throw new InvalidDataException("コピー先の空き枠がありません。");
            }
            draft.Copy(slots.SelectedIndex, target);
            Refresh(target);
        }));
        SchedulerEntryWindow.Button(edits, "選択を削除", "ScheduleDelete", () => TryEdit(() =>
        {
            draft.SetEntry(slots.SelectedIndex, null);
            Refresh(slots.SelectedIndex);
        }));
        SchedulerEntryWindow.Button(edits, "上へ移動", "ScheduleUp", () => Move(-1));
        SchedulerEntryWindow.Button(edits, "下へ移動", "ScheduleDown", () => Move(1));
        SchedulerEntryWindow.Button(edits, "明示再予約を指定", "ScheduleRearm", () => TryEdit(() =>
        {
            draft.Rearm(slots.SelectedIndex);
            Refresh(slots.SelectedIndex);
        }));
        SchedulerEntryWindow.Identify(slots, "ScheduleSlots");
        AutomationProperties.SetName(slots, "予約の登録位置");
        slots.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(slots);
        slots.MouseDoubleClick += (_, _) => Edit();
        Refresh(0);
    }

    private void Edit() => TryEdit(() =>
    {
        int index = slots.SelectedIndex;
        if (index < 0)
        {
            return;
        }
        var dialog = new SchedulerEntryWindow(draft.Settings.Slots[index], applicationDirectory) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            draft.SetEntry(index, dialog.Result);
            Refresh(index);
        }
    });

    private void Move(int direction) => TryEdit(() =>
    {
        int index = slots.SelectedIndex;
        int target = index + direction;
        if (index >= 0 && target >= 0 && target < draft.Settings.Slots.Count)
        {
            draft.Move(index, target);
            Refresh(target);
        }
    });

    private void Refresh(int selected)
    {
        slots.Items.Clear();
        int index = 0;
        foreach (SchedulerEntry? entry in draft.Settings.Slots)
        {
            string state = entry is null ? "空き" : entry.Enabled ? "有効" : "無効";
            string text = entry is null ? "" : entry.Title + " / "
                + (entry.Mode == SchedulerExecutionMode.SameProcess ? "同一プロセス" : "別プロセス")
                + " / " + ConditionText(entry.Condition);
            if (entry is not null && draft.IsRearmed(entry.Id))
            {
                text += " / 明示再予約を適用予定";
            }
            else if (entry is not null && draft.Settings.OneShotStates.Any(value => value.EntryId == entry.Id))
            {
                text += " / 一回消費済み";
            }
            slots.Items.Add($"{++index:000} [{state}] {text}");
        }
        slots.SelectedIndex = Math.Clamp(selected, 0, slots.Items.Count - 1);
    }

    private void TryEdit(Action action)
    {
        try
        {
            action();
            error.Text = "";
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            error.Text = failure.Message;
        }
    }

    internal static string ConditionText(SchedulerCondition condition) => condition switch
    {
        SchedulerStartupCondition => "起動時",
        SchedulerIntervalCondition interval => $"間隔 {interval.Value}"
            + (interval.Unit == SchedulerIntervalUnit.Minute ? "分" : "時間"),
        SchedulerTimeCondition time => $"指定時刻 {time.Time:HH:mm} / "
            + (time.DayKind == SchedulerDayKind.EveryDay ? "毎日"
                : time.DayKind == SchedulerDayKind.Weekdays ? "曜日 " : "月内の日付 ")
            + string.Join(",", time.Days),
        SchedulerExpressionCondition expression => "式: " + expression.Source,
        SchedulerOneShotCondition once => $"一回 {once.Date:yyyy-MM-dd} {once.Time:HH:mm}",
        SchedulerWindowCondition window => $"窓: {window.Title} / {window.ClassName}",
        _ => "未対応の条件"
    };
}
