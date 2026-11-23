using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace UWSCSharp.Wpf;

/// <summary>
/// 実行対象と型付き条件を編集し、反映までは元の登録を変更しません。
/// </summary>
internal sealed class SchedulerEntryWindow : Window
{
    internal SchedulerEntry? Result
    {
        get; private set;
    }

    internal SchedulerEntryWindow(SchedulerEntry? current, string applicationDirectory)
    {
        Title = "予約の登録内容";
        Width = 720;
        Height = 780;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        MinWidth = 560;
        MinHeight = Math.Min(500, MaxHeight);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Identify(error, "ScheduleEntryError");
        footer.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(actions);
        Button accept = Button(actions, "反映", "ScheduleEntryApply");
        accept.IsDefault = true;
        Button cancel = Button(actions, "取消", "ScheduleEntryCancel", () => { });
        cancel.IsCancel = true;
        var form = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var enabled = new CheckBox { Content = "この予約を有効にする", IsChecked = current?.Enabled ?? true };
        Identify(enabled, "ScheduleEntryEnabled");
        form.Children.Add(enabled);
        TextBox title = Text(form, "表示名", "ScheduleEntryTitle", current?.Title ?? "");
        TextBox path = Text(form, "スクリプト（.uws）", "ScheduleEntryPath", current?.Path ?? "");
        Button(form, "ファイルを参照…", "ScheduleEntryBrowse", () =>
        {
            var dialog = new OpenFileDialog { Filter = "UWSCスクリプト|*.uws", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true)
            {
                path.Text = dialog.FileName;
            }
        });
        form.Children.Add(new TextBlock { Text = "相対パスはUWSC#の配置フォルダーを基準にし、登録時に絶対パスへ変換します。", TextWrapping = TextWrapping.Wrap });
        ComboBox mode = Choice(form, "実行方式", "ScheduleEntryMode", ["同一プロセス", "別プロセス"], (int)(current?.Mode ?? SchedulerExecutionMode.SameProcess));
        int initialKind = current?.Condition switch
        {
            SchedulerIntervalCondition => 1,
            SchedulerTimeCondition => 2,
            SchedulerExpressionCondition => 3,
            SchedulerOneShotCondition => 4,
            SchedulerWindowCondition => 5,
            _ => 0
        };
        ComboBox kind = Choice(form, "条件", "ScheduleEntryCondition", ["起動時", "間隔", "指定時刻", "数値式", "一回", "ウィンドウ"], initialKind);
        var conditions = new StackPanel[6];
        for (int index = 0; index < conditions.Length; index++)
        {
            conditions[index] = new StackPanel();
            form.Children.Add(conditions[index]);
        }
        conditions[0].Children.Add(new TextBlock { Text = "この本体の監視開始時に受け付けます。" });
        var interval = current?.Condition as SchedulerIntervalCondition;
        TextBox intervalValue = Text(conditions[1], "間隔の値", "ScheduleIntervalValue", (interval?.Value ?? 1).ToString(CultureInfo.InvariantCulture));
        ComboBox intervalUnit = Choice(conditions[1], "単位（分1〜60、時間1〜24）", "ScheduleIntervalUnit", ["分", "時間"], (int)(interval?.Unit ?? SchedulerIntervalUnit.Minute));
        var time = current?.Condition as SchedulerTimeCondition;
        TextBox timeValue = Text(conditions[2], "時刻（HH:mm）", "ScheduleTime", time?.Time.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "00:00");
        ComboBox dayKind = Choice(conditions[2], "日付の種類", "ScheduleDayKind", ["毎日", "曜日", "月内の日付"], (int)(time?.DayKind ?? SchedulerDayKind.EveryDay));
        TextBox days = Text(conditions[2], "曜日は日曜0〜土曜6、日付は1〜31をカンマ区切り", "ScheduleDays", string.Join(",", time?.Days ?? []));
        TextBox expression = Text(conditions[3], "数値式（YY・MM・DD・HH・NN・WW・YMDNN）", "ScheduleExpression", (current?.Condition as SchedulerExpressionCondition)?.Source ?? "1");
        var oneShot = current?.Condition as SchedulerOneShotCondition;
        DateTime next = DateTime.Now.AddMinutes(1);
        TextBox date = Text(conditions[4], "日付（yyyy-MM-dd）", "ScheduleDate", oneShot?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        TextBox onceTime = Text(conditions[4], "時刻（HH:mm）", "ScheduleOnceTime", oneShot?.Time.ToString("HH:mm", CultureInfo.InvariantCulture) ?? next.ToString("HH:mm", CultureInfo.InvariantCulture));
        conditions[4].Children.Add(new TextBlock { Text = "新規・日時変更・再予約は、設定を保存する時点で現在の分より後の日時が必要です。", TextWrapping = TextWrapping.Wrap });
        var window = current?.Condition as SchedulerWindowCondition;
        TextBox windowTitle = Text(conditions[5], "タイトルの部分一致", "ScheduleWindowTitle", window?.Title ?? "");
        TextBox windowClass = Text(conditions[5], "クラス名の完全一致", "ScheduleWindowClass", window?.ClassName ?? "");
        conditions[5].Children.Add(new TextBlock { Text = "ウィンドウ条件の監視は未接続です。ここでは登録内容だけを保存します。", TextWrapping = TextWrapping.Wrap });

        form.Children.Add(new TextBlock { Text = "追加引数（一覧の1項目を1つの引数として渡します）", Margin = new Thickness(0, 12, 0, 4) });
        var arguments = new List<string>(current?.Arguments ?? []);
        var list = new ListBox { Height = 100 };
        Identify(list, "ScheduleArguments");
        form.Children.Add(list);
        TextBox argument = Text(form, "引数の値（空欄の追加は空文字列）", "ScheduleArgumentValue", "");
        var edits = new WrapPanel();
        form.Children.Add(edits);
        Button(edits, "追加", "ScheduleArgumentAdd", () => { arguments.Add(argument.Text); RefreshArguments(arguments.Count - 1); });
        Button(edits, "選択を変更", "ScheduleArgumentChange", () =>
        {
            if (list.SelectedIndex >= 0)
            {
                int selected = list.SelectedIndex;
                arguments[selected] = argument.Text;
                RefreshArguments(selected);
            }
        });
        Button(edits, "削除", "ScheduleArgumentDelete", () =>
        {
            if (list.SelectedIndex >= 0)
            {
                int selected = list.SelectedIndex;
                arguments.RemoveAt(selected);
                RefreshArguments(Math.Min(selected, arguments.Count - 1));
            }
        });
        Button(edits, "上へ", "ScheduleArgumentUp", () => MoveArgument(-1));
        Button(edits, "下へ", "ScheduleArgumentDown", () => MoveArgument(1));
        list.SelectionChanged += (_, _) => { if (list.SelectedIndex >= 0) { argument.Text = arguments[list.SelectedIndex]; } };
        kind.SelectionChanged += (_, _) => RefreshCondition();
        RefreshCondition();
        RefreshArguments(arguments.Count > 0 ? 0 : -1);
        accept.Click += (_, _) => Apply();

        void Apply()
        {
            try
            {
                SchedulerCondition condition = kind.SelectedIndex switch
                {
                    0 => new SchedulerStartupCondition(),
                    1 => new SchedulerIntervalCondition((SchedulerIntervalUnit)intervalUnit.SelectedIndex, int.Parse(intervalValue.Text, CultureInfo.InvariantCulture)),
                    2 => new SchedulerTimeCondition(ParseTime(timeValue.Text), (SchedulerDayKind)dayKind.SelectedIndex,
                        dayKind.SelectedIndex == 0 ? [] : days.Text.Split(',', StringSplitOptions.TrimEntries).Select(value => int.Parse(value, CultureInfo.InvariantCulture))),
                    3 => new SchedulerExpressionCondition(expression.Text),
                    4 => new SchedulerOneShotCondition(DateOnly.ParseExact(date.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture), ParseTime(onceTime.Text)),
                    5 => new SchedulerWindowCondition(windowTitle.Text, windowClass.Text),
                    _ => throw new InvalidDataException("予約の条件を選択してください。")
                };
                Result = new SchedulerEntry(current?.Id ?? Guid.NewGuid(), title.Text, path.Text,
                    arguments, (SchedulerExecutionMode)mode.SelectedIndex, condition,
                    applicationDirectory, enabled.IsChecked == true);
                DialogResult = true;
            }
            catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or FormatException or OverflowException or NotSupportedException)
            {
                error.Text = failure.Message;
            }
        }

        void RefreshCondition()
        {
            for (int index = 0; index < conditions.Length; index++)
            {
                conditions[index].Visibility = index == kind.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        void RefreshArguments(int selected)
        {
            list.Items.Clear();
            foreach (string value in arguments)
            {
                list.Items.Add(value.Length == 0 ? "（空文字列）" : value);
            }
            list.SelectedIndex = selected;
        }

        void MoveArgument(int direction)
        {
            int selected = list.SelectedIndex;
            int nextIndex = selected + direction;
            if (selected >= 0 && nextIndex >= 0 && nextIndex < arguments.Count)
            {
                (arguments[selected], arguments[nextIndex]) = (arguments[nextIndex], arguments[selected]);
                RefreshArguments(nextIndex);
            }
        }
    }

    private static TimeOnly ParseTime(string value) =>
        TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);

    internal static void Identify(DependencyObject element, string id) =>
        AutomationProperties.SetAutomationId(element, id);

    internal static TextBox Text(Panel panel, string label, string id, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        var input = new TextBox { Text = value };
        Identify(input, id);
        AutomationProperties.SetName(input, label);
        panel.Children.Add(input);
        return input;
    }

    internal static ComboBox Choice(Panel panel, string label, string id, string[] values, int selected)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        var input = new ComboBox { ItemsSource = values, SelectedIndex = selected };
        Identify(input, id);
        AutomationProperties.SetName(input, label);
        panel.Children.Add(input);
        return input;
    }

    internal static Button Button(Panel panel, string text, string id, Action? action = null)
    {
        var button = new Button { Content = text, Margin = new Thickness(2, 6, 2, 4), Padding = new Thickness(10, 5, 10, 5) };
        Identify(button, id);
        if (action is not null)
        {
            button.Click += (_, _) => action();
        }
        panel.Children.Add(button);
        return button;
    }
}
