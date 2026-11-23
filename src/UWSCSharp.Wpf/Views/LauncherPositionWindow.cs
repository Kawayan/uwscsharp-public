using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UWSCSharp.IO;

namespace UWSCSharp.Wpf;

internal sealed class LauncherPositionWindow : Window
{
    private readonly string? settingsPath;
    private readonly TextBox x = new();
    private readonly TextBox y = new();
    private readonly CheckBox cursor = new()
    {
        Content = "カーソル位置に表示する"
    };
    private readonly TextBlock error = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button save = new()
    {
        Content = "保存",
        IsDefault = true,
        IsEnabled = false
    };
    private bool saving;
    private bool loaded;

    internal ApplicationLauncherSettings? Result
    {
        get; private set;
    }

    internal LauncherPositionWindow(string? settingsPath = null)
    {
        this.settingsPath = settingsPath;
        Title = "ランチの表示位置";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxHeight = SystemParameters.WorkArea.Height;
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        panel.Children.Add(new TextBlock
        {
            Text = "ホットキーで開くランチの位置を指定します。ボタンと通知領域から開く位置は変わりません。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(cursor);
        AddField("横座標", x);
        AddField("縦座標", y);
        panel.Children.Add(error);
        AutomationProperties.SetName(error, "ランチ位置のエラー");
        panel.Children.Add(save);
        panel.Children.Add(new Button
        {
            Content = "取消",
            IsCancel = true,
            Margin = new Thickness(0, 4, 0, 0)
        });
        cursor.Checked += (_, _) => RefreshControls();
        cursor.Unchecked += (_, _) => RefreshControls();
        Loaded += async (_, _) => await LoadAsync();
        save.Click += async (_, _) => await SaveAsync();
        Closing += (_, args) => args.Cancel = saving;
        RefreshControls();

        void AddField(string label, TextBox box)
        {
            panel.Children.Add(new TextBlock { Text = label });
            box.Margin = new Thickness(0, 4, 0, 8);
            AutomationProperties.SetName(box, label);
            panel.Children.Add(box);
        }
    }

    private void RefreshControls()
    {
        cursor.IsEnabled = loaded && !saving;
        x.IsEnabled = loaded && !saving && cursor.IsChecked != true;
        y.IsEnabled = x.IsEnabled;
        save.IsEnabled = loaded && !saving;
    }

    private async Task LoadAsync()
    {
        try
        {
            ApplicationLauncherSettings settings =
                await ApplicationLauncherSettings.LoadAsync(settingsPath);
            x.Text = settings.X.ToString(CultureInfo.InvariantCulture);
            y.Text = settings.Y.ToString(CultureInfo.InvariantCulture);
            cursor.IsChecked = settings.UseCursorPosition;
            loaded = true;
            RefreshControls();
        }
        catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException)
        {
            error.Text = "ランチの表示位置を読み込めませんでした。" + failure.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (!loaded || saving)
        {
            return;
        }

        try
        {
            var settings = new ApplicationLauncherSettings();
            if (cursor.IsChecked != true)
            {
                if (!int.TryParse(x.Text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int left)
                    || !int.TryParse(y.Text, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int top))
                {
                    throw new ArgumentException("横座標と縦座標は整数で指定してください。");
                }

                settings = new ApplicationLauncherSettings(left, top);
            }

            saving = true;
            RefreshControls();
            await settings.SaveAsync(settingsPath);
            Result = settings;
            saving = false;
            DialogResult = true;
        }
        catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException)
        {
            error.Text = "ランチの表示位置を保存できませんでした。" + failure.Message;
        }
        finally
        {
            saving = false;
            RefreshControls();
        }
    }
}
