using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace UWSCSharp.Wpf;

internal sealed class HotkeySettingsWindow : Window
{
    internal HotkeySettings? Result
    {
        get; private set;
    }

    internal HotkeySettingsWindow(HotkeySettings current)
    {
        Title = "ホットキー設定";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = panel;
        var enabled = new CheckBox
        {
            Content = "ホットキーを有効にする",
            IsChecked = current.Enabled
        };
        panel.Children.Add(enabled);
        var modifiers = new ComboBox { Margin = new Thickness(0, 8, 0, 8) };
        AutomationProperties.SetName(modifiers, "修飾キー");
        uint[] values = [.. Enumerable.Range(0, 16).Select(value => (uint)value)];
        modifiers.ItemsSource = values.Select(value =>
        {
            string label = ((value & 2) != 0 ? "Ctrl+" : "")
                + ((value & 1) != 0 ? "Alt+" : "")
                + ((value & 4) != 0 ? "Shift+" : "")
                + ((value & 8) != 0 ? "Win+" : "");
            return label.Length == 0 ? "修飾キーなし" : label.TrimEnd('+');
        });
        modifiers.SelectedIndex = Math.Max(0, Array.IndexOf(values, current.Modifiers));
        panel.Children.Add(modifiers);
        var boxes = new List<ComboBox>();
        string[] labels = ["記録", "一時停止・再開", "停止", "再生開始"];
        for (int index = 0; index < labels.Length; index++)
        {
            panel.Children.Add(new TextBlock { Text = labels[index] });
            var box = new ComboBox
            {
                ItemsSource = Enumerable.Range(1, 11).Select(number => $"F{number}"),
                SelectedIndex = current.Keys[index] - 112,
                Margin = new Thickness(0, 4, 0, 8)
            };
            boxes.Add(box);
            AutomationProperties.SetName(box, labels[index] + "キー");
            panel.Children.Add(box);
        }

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var save = new Button
        {
            Content = "保存",
            IsDefault = true,
            Padding = new Thickness(8)
        };
        panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            var candidate = new HotkeySettings(enabled.IsChecked == true,
                values[modifiers.SelectedIndex], boxes[0].SelectedIndex + 112,
                boxes[1].SelectedIndex + 112, boxes[2].SelectedIndex + 112,
                boxes[3].SelectedIndex + 112);
            try
            {
                candidate.Validate();
                Result = candidate;
                DialogResult = true;
            }
            catch (System.IO.InvalidDataException exception)
            {
                error.Text = exception.Message;
            }
        };
    }
}
