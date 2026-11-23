using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UWSCSharp.IO;

namespace UWSCSharp.Wpf;

internal sealed class LogSettingsWindow : Window
{
    private readonly TextBox lines = new();
    private readonly TextBox family = new();
    private readonly TextBox size = new();
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button save = new()
    {
        Content = "保存",
        IsDefault = true,
        IsEnabled = false,
        Padding = new Thickness(8)
    };
    private readonly string? settingsPath;
    private bool saving;

    internal LogSettingsWindow(string? settingsPath = null)
    {
        this.settingsPath = settingsPath;
        SetEditorEnabled(false);
        Title = "ログ設定";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NameScope.SetNameScope(this, new NameScope());
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = panel;
        AddField(panel, "最大行数（1～100000）", "MaximumLines", lines);
        AddField(panel, "ログのフォント名", "FontFamilyName", family);
        AddField(panel, "文字サイズ（pt）", "FontSizePoints", size);
        panel.Children.Add(new TextBlock
        {
            Text = "次の実行から適用します。スクリプト内のOPTIONを優先します。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8)
        });
        AutomationProperties.SetName(error, "ログ設定のエラー");
        RegisterName("SettingsError", error);
        panel.Children.Add(error);
        RegisterName("SaveSettings", save);
        panel.Children.Add(save);
        panel.Children.Add(new Button
        {
            Content = "キャンセル",
            IsCancel = true,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0)
        });
        Loaded += async (_, _) => await LoadSettingsAsync();
        save.Click += async (_, _) => await SaveSettingsAsync();
        Closing += (_, args) => args.Cancel = saving;
    }

    private void AddField(Panel panel, string label, string name, TextBox box)
    {
        panel.Children.Add(new TextBlock { Text = label });
        box.Margin = new Thickness(0, 4, 0, 8);
        AutomationProperties.SetName(box, label);
        RegisterName(name, box);
        panel.Children.Add(box);
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            ScriptLogDefaults current = await ScriptLogDefaults.LoadAsync(settingsPath);
            lines.Text = current.MaximumLines.ToString(CultureInfo.InvariantCulture);
            family.Text = current.Font.Family;
            size.Text = current.Font.SizePoints.ToString("R", CultureInfo.InvariantCulture);
            SetEditorEnabled(true);
            save.IsEnabled = true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error.Text = "設定を読み込めませんでした。" + failure.Message;
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (saving)
        {
            return;
        }

        try
        {
            if (!int.TryParse(lines.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int maximum)
                || maximum is < 1 or > 100_000)
            {
                throw new ArgumentException("最大行数は1～100000の整数で指定してください。");
            }

            if (!double.TryParse(size.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double points))
            {
                throw new ArgumentException("文字サイズは正の数で指定してください。");
            }

            var current = new ScriptLogDefaults(maximum, new ScriptLogFont(family.Text, points));
            saving = true;
            SetEditorEnabled(false);
            save.IsEnabled = false;
            await current.SaveAsync(settingsPath);
            saving = false;
            DialogResult = true;
        }
        catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException)
        {
            error.Text = "設定を保存できませんでした。" + failure.Message;
        }
        finally
        {
            saving = false;
            SetEditorEnabled(true);
            save.IsEnabled = true;
        }
    }

    private void SetEditorEnabled(bool enabled)
    {
        lines.IsEnabled = enabled;
        family.IsEnabled = enabled;
        size.IsEnabled = enabled;
    }
}
