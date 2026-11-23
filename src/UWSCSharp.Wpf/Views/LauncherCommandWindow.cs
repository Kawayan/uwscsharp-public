using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace UWSCSharp.Wpf;

internal sealed class LauncherCommandWindow : Window
{
    internal LauncherCommand? Result
    {
        get; private set;
    }

    internal LauncherCommandWindow(LauncherCommand? current,
        string applicationDirectory)
    {
        Title = "ランチの起動項目";
        Width = 720;
        Height = 620;
        MinWidth = 560;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        Content = panel;
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        AutomationProperties.SetName(error, "項目の入力エラー");
        footer.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = "反映", IsDefault = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(4) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(4) };
        actions.Children.Add(accept);
        actions.Children.Add(cancel);
        footer.Children.Add(actions);

        var form = new StackPanel();
        DockPanel.SetDock(form, Dock.Top);
        panel.Children.Add(form);
        form.Children.Add(new TextBlock { Text = "タイトル" });
        var title = new TextBox { Text = current?.Title ?? "", Margin = new Thickness(0, 4, 0, 12) };
        AutomationProperties.SetName(title, "項目のタイトル");
        form.Children.Add(title);
        form.Children.Add(new TextBlock { Text = "対象ファイル" });
        var pathRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var browse = new Button { Content = "参照", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        var path = new TextBox { Text = current?.Path ?? "", VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(path, "対象ファイルのパス");
        pathRow.Children.Add(path);
        form.Children.Add(pathRow);
        var kind = new TextBlock { Margin = new Thickness(0, 2, 0, 4) };
        form.Children.Add(kind);
        form.Children.Add(new TextBlock
        {
            Text = "相対パスはUWSC#の配置フォルダーを基準にします。登録後は絶対パスで保存します。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        form.Children.Add(new TextBlock { Text = "追加引数（一覧の1項目を1つの引数として渡します）" });

        var arguments = new List<string>(current?.Arguments ?? []);
        var argumentPanel = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(argumentPanel);
        var editor = new StackPanel();
        DockPanel.SetDock(editor, Dock.Bottom);
        argumentPanel.Children.Add(editor);
        var argument = new TextBox { Margin = new Thickness(0, 8, 0, 4) };
        AutomationProperties.SetName(argument, "引数の値");
        editor.Children.Add(argument);
        var edits = new WrapPanel();
        editor.Children.Add(edits);
        var list = new ListBox();
        AutomationProperties.SetName(list, "順序付きの引数");
        argumentPanel.Children.Add(list);
        AddAction("追加", () =>
        {
            arguments.Add(argument.Text);
            Refresh(arguments.Count - 1);
        });
        AddAction("選択を変更", () =>
        {
            if (list.SelectedIndex >= 0)
            {
                int selected = list.SelectedIndex;
                arguments[selected] = argument.Text;
                Refresh(selected);
            }
        });
        AddAction("削除", () =>
        {
            if (list.SelectedIndex >= 0)
            {
                int selected = list.SelectedIndex;
                arguments.RemoveAt(selected);
                Refresh(Math.Min(selected, arguments.Count - 1));
            }
        });
        AddAction("上へ", () => Move(-1));
        AddAction("下へ", () => Move(1));
        editor.Children.Add(new TextBlock
        {
            Text = "空欄のまま追加すると空文字列を渡します。既定アプリで開くファイルには引数を指定できません。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0)
            {
                argument.Text = arguments[list.SelectedIndex];
            }
        };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Filter = "すべてのファイル (*.*)|*.*|UWSCスクリプト (*.uws)|*.uws|実行ファイル (*.exe)|*.exe",
                Title = "ランチに登録するファイル"
            };
            if (dialog.ShowDialog(this) == true)
            {
                path.Text = dialog.FileName;
                if (string.IsNullOrWhiteSpace(title.Text))
                {
                    title.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
                }
            }
        };
        path.TextChanged += (_, _) => RefreshKind();
        accept.Click += (_, _) =>
        {
            try
            {
                Result = new LauncherCommand(title.Text, path.Text,
                    GetKind(), arguments, applicationDirectory);
                DialogResult = true;
            }
            catch (Exception failure) when (failure is ArgumentException
                or IOException or InvalidDataException or NotSupportedException)
            {
                error.Text = failure.Message;
            }
        };
        RefreshKind();
        Refresh(arguments.Count > 0 ? 0 : -1);

        LauncherCommandKind GetKind()
        {
            string extension = Path.GetExtension(path.Text);
            return extension.Equals(".uws", StringComparison.OrdinalIgnoreCase)
                ? LauncherCommandKind.Script
                : extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    ? LauncherCommandKind.Executable : LauncherCommandKind.Document;
        }

        void RefreshKind()
        {
            kind.Text = GetKind() switch
            {
                LauncherCommandKind.Script => "種別：スクリプト（別プロセスで実行）",
                LauncherCommandKind.Executable => "種別：実行ファイル",
                _ => "種別：既定アプリで開くファイル"
            };
        }

        void Refresh(int selected)
        {
            list.Items.Clear();
            foreach (string value in arguments)
            {
                list.Items.Add(value.Length == 0 ? "（空文字列）" : value);
            }

            list.SelectedIndex = selected;
        }

        void Move(int direction)
        {
            int selected = list.SelectedIndex;
            int next = selected + direction;
            if (selected >= 0 && next >= 0 && next < arguments.Count)
            {
                (arguments[selected], arguments[next]) = (arguments[next], arguments[selected]);
                Refresh(next);
            }
        }

        void AddAction(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (_, _) => action();
            edits.Children.Add(button);
        }
    }
}
