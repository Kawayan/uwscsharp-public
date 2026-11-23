using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

internal sealed class LauncherSettingsWindow : Window
{
    private LauncherSettings draft;
    private int? parent;
    private readonly ListBox slots = new();
    private readonly TextBlock location = new();
    private readonly TextBlock error = new();
    private readonly string applicationDirectory;
    private readonly Button back = new() { Content = "上位へ戻る" };
    private readonly Button group = new() { Content = "階層を作成・名称変更" };
    private readonly Button enter = new() { Content = "階層を開く" };

    internal LauncherSettingsWindow(LauncherSettings current,
        string applicationDirectory, Action<LauncherSettings> save)
    {
        draft = current;
        this.applicationDirectory = applicationDirectory;
        Title = "ランチの登録";
        Width = 760;
        Height = 640;
        MinWidth = 600;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        Content = panel;
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        top.Children.Add(new TextBlock
        {
            Text = "各階層は35枠です。空き位置も保存します。変更は「設定を保存」で確定します。",
            TextWrapping = TextWrapping.Wrap
        });
        var shortcutRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var shortcutEnabled = new CheckBox
        {
            Content = "ランチ用ホットキー",
            IsChecked = current.Shortcut is not null,
            VerticalAlignment = VerticalAlignment.Center
        };
        int[] modifierValues = [.. Enumerable.Range(1, 15)];
        int[] keyValues = [.. Enumerable.Range(48, 10),
            .. Enumerable.Range(65, 26), .. Enumerable.Range(112, 24)];
        var shortcutModifiers = new ComboBox
        {
            ItemsSource = modifierValues.Select(DescribeModifiers),
            SelectedIndex = (current.Shortcut?.Modifiers ?? 2) - 1,
            Margin = new Thickness(8, 0, 8, 0),
            MinWidth = 120
        };
        var shortcutKey = new ComboBox
        {
            ItemsSource = keyValues.Select(DescribeKey),
            MinWidth = 70,
            SelectedIndex = Array.IndexOf(keyValues, current.Shortcut?.VirtualKey ?? 65)
        };
        AutomationProperties.SetName(shortcutModifiers, "ランチの修飾キー");
        AutomationProperties.SetName(shortcutKey, "ランチのキー");
        shortcutRow.Children.Add(shortcutEnabled);
        shortcutRow.Children.Add(shortcutModifiers);
        shortcutRow.Children.Add(shortcutKey);
        top.Children.Add(shortcutRow);
        location.Margin = new Thickness(0, 10, 0, 8);
        top.Children.Add(location);
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        error.TextWrapping = TextWrapping.Wrap;
        error.Margin = new Thickness(0, 8, 0, 8);
        AutomationProperties.SetName(error, "登録設定のエラー");
        footer.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = "設定を保存", IsDefault = true };
        var cancel = new Button { Content = "取消", IsCancel = true };
        actions.Children.Add(accept);
        actions.Children.Add(cancel);
        footer.Children.Add(actions);
        var edits = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(edits, Dock.Right);
        panel.Children.Add(edits);
        AddAction("起動項目を登録・編集", EditCommand);
        edits.Children.Add(group);
        edits.Children.Add(enter);
        edits.Children.Add(back);
        AddAction("選択を削除", Delete);
        AddAction("上へ移動", () => Move(-1));
        AddAction("下へ移動", () => Move(1));
        foreach (Button button in edits.Children.OfType<Button>().Concat([accept, cancel]))
        {
            button.Margin = new Thickness(0, 0, 6, 8);
            button.Padding = new Thickness(12, 6, 12, 6);
        }

        AutomationProperties.SetName(slots, "ランチの登録位置");
        panel.Children.Add(slots);
        slots.SelectionChanged += (_, _) => RefreshActions();
        group.Click += (_, _) => EditGroup();
        enter.Click += (_, _) =>
        {
            if (parent is null && Selected() is LauncherGroup)
            {
                parent = slots.SelectedIndex;
                Refresh(0);
            }
        };
        back.Click += (_, _) =>
        {
            int selected = parent ?? 0;
            parent = null;
            Refresh(selected);
        };
        accept.Click += (_, _) =>
        {
            try
            {
                if (shortcutEnabled.IsChecked == true
                    && (shortcutKey.SelectedIndex < 0 || shortcutModifiers.SelectedIndex < 0))
                {
                    throw new System.IO.InvalidDataException("ランチ用ホットキーを選択してください。");
                }

                ScriptHotkey? shortcut = shortcutEnabled.IsChecked == true
                    ? new ScriptHotkey(keyValues[shortcutKey.SelectedIndex],
                        modifierValues[shortcutModifiers.SelectedIndex]) : null;
                var candidate = new LauncherSettings(draft.Slots, shortcut);
                save(candidate);
                DialogResult = true;
            }
            catch (Exception failure) when (failure is System.IO.IOException or System.IO.InvalidDataException
                or UnauthorizedAccessException or ArgumentException
                or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                error.Text = failure.Message;
            }
        };
        Refresh(0);

        void AddAction(string text, Action action)
        {
            var button = new Button { Content = text };
            button.Click += (_, _) => action();
            edits.Children.Add(button);
        }
    }

    internal static string DescribeShortcut(ScriptHotkey shortcut) =>
        DescribeModifiers(shortcut.Modifiers) + "+" + DescribeKey(shortcut.VirtualKey);

    private static string DescribeModifiers(int modifiers) => string.Join("+",
        new[] { (2, "Ctrl"), (1, "Alt"), (4, "Shift"), (8, "Win") }
            .Where(item => (modifiers & item.Item1) != 0).Select(item => item.Item2));

    private static string DescribeKey(int key) => key is >= 112 and <= 135
        ? "F" + (key - 111) : ((char)key).ToString();

    private LauncherEntry? Selected() => slots.SelectedIndex < 0 ? null
        : parent is int index
            ? ((LauncherGroup)draft.Slots[index]!).Slots[slots.SelectedIndex]
            : draft.Slots[slots.SelectedIndex];

    private void Set(LauncherEntry? entry)
    {
        int selected = slots.SelectedIndex;
        if (selected < 0)
        {
            return;
        }

        draft = parent is int index
            ? draft.WithSlot(index, ((LauncherGroup)draft.Slots[index]!)
                .WithSlot(selected, (LauncherCommand?)entry))
            : draft.WithSlot(selected, entry);
        Refresh(selected);
    }

    private void EditCommand()
    {
        if (slots.SelectedIndex < 0 || !ConfirmReplaceGroup())
        {
            return;
        }

        var dialog = new LauncherCommandWindow(Selected() as LauncherCommand,
            applicationDirectory)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            Set(dialog.Result);
        }
    }

    private void EditGroup()
    {
        if (parent is not null || slots.SelectedIndex < 0)
        {
            return;
        }

        var dialog = new Window
        {
            Title = "階層の名称",
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        dialog.Content = panel;
        var name = new TextBox { Text = Selected()?.Title ?? "" };
        AutomationProperties.SetName(name, "階層の名称");
        panel.Children.Add(name);
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(message);
        var accept = new Button { Content = "反映", IsDefault = true, Padding = new Thickness(12, 6, 12, 6) };
        panel.Children.Add(accept);
        accept.Click += (_, _) =>
        {
            try
            {
                Set(new LauncherGroup(name.Text, (Selected() as LauncherGroup)?.Slots ?? []));
                dialog.DialogResult = true;
            }
            catch (Exception failure) when (failure is ArgumentException
                or System.IO.IOException or System.IO.InvalidDataException)
            {
                message.Text = failure.Message;
            }
        };
        dialog.ShowDialog();
    }

    private bool ConfirmReplaceGroup() => Selected() is not LauncherGroup group
        || group.Slots.All(item => item is null)
        || MessageBox.Show(this, "この階層内の登録も削除しますか？", "ランチの登録",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void Delete()
    {
        if (ConfirmReplaceGroup())
        {
            Set(null);
        }
    }

    private void Move(int direction)
    {
        int selected = slots.SelectedIndex;
        int next = selected + direction;
        if (selected < 0 || next < 0 || next >= LauncherSettings.SlotCount)
        {
            return;
        }

        draft = parent is int index
            ? draft.WithSlot(index, ((LauncherGroup)draft.Slots[index]!).MoveSlot(selected, next))
            : draft.MoveSlot(selected, next);
        Refresh(next);
    }

    private void Refresh(int selected)
    {
        IEnumerable<LauncherEntry?> entries = parent is int index
            ? ((LauncherGroup)draft.Slots[index]!).Slots : draft.Slots;
        location.Text = parent is int groupIndex
            ? "上位 ／ " + draft.Slots[groupIndex]!.Title : "上位の登録";
        slots.Items.Clear();
        int position = 1;
        foreach (LauncherEntry? entry in entries)
        {
            string label = entry switch
            {
                LauncherGroup value => "［階層］" + value.Title,
                LauncherCommand value => value.Title,
                _ => "（空き）"
            };
            slots.Items.Add($"{position++:00}  {label}");
        }

        slots.SelectedIndex = selected;
        RefreshActions();
    }

    private void RefreshActions()
    {
        back.IsEnabled = parent is not null;
        group.IsEnabled = parent is null && slots.SelectedIndex >= 0;
        enter.IsEnabled = parent is null && Selected() is LauncherGroup;
    }
}
