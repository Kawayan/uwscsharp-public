using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private LauncherSettingsController launcher = null!;
    private Action<LauncherCommand>? launchCommandOverride;
    private ContextMenu? launcherMenu;
    private bool launcherClosed;
    private Func<bool>? confirmLauncherReset;
    private LauncherHotkeyRegistration? launcherHotkeys;
    private IHotkeyBackend? launcherHotkeyBackendOverride;
    private string? launcherPositionSettingsPath;

    private bool CanEditLauncher => !commandLine && IsEnabled
        && cancellation is null && !recording.HasRecorder
        && !recording.IsStarting && !recording.IsStopping
        && scheduledPlayback is null && launcherHotkeys?.IsBusy != true;

    private void InitializeLauncher(object? sender, EventArgs e)
    {
        if (!commandLine)
        {
            launcherHotkeys = new LauncherHotkeyRegistration(
                launcherHotkeyBackendOverride ?? new WindowHotkeyBackend(windowHandle));
            ReloadLauncher();
        }

        RefreshLauncherControls();
    }

    private void RefreshLauncherControls()
    {
        LauncherButton.IsEnabled = !commandLine;
    }

    private void ReloadLauncher()
    {
        if (!CanEditLauncher)
        {
            return;
        }

        try
        {
            launcher.ReloadWithHotkey(candidate =>
                launcherHotkeys?.Apply(candidate, MainShortcuts(), () => { }));
            LauncherNotice.Text = "";
        }
        catch (Exception error) when (IsLauncherError(error))
        {
            LauncherNotice.Text = (launcher.LoadError is null
                ? "ランチ用ホットキーを適用できません。登録内容は保持しています。"
                : "ランチ設定を読み込めません。元のファイルは保持しています。")
                + Environment.NewLine + error.Message;
        }
        finally
        {
            RefreshLauncherHotkeyNotice();
        }
    }

    private void LauncherClick(object sender, RoutedEventArgs e) => OpenLauncher();

    private void OpenLauncher(bool atPointer = false,
        ApplicationLauncherSettings? position = null)
    {
        if (commandLine || !IsEnabled || launcherClosed)
        {
            return;
        }

        launcherMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = new ContextMenu();
        LauncherSettings? current = launcher.Current;
        if (current is not null)
        {
            AddEntries(menu.Items, current.Slots);
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "登録された項目はありません", IsEnabled = false });
        }

        menu.Items.Add(new Separator());
        var settings = new MenuItem
        {
            Header = "登録を編集…",
            IsEnabled = CanEditLauncher && launcher.LoadError is null
        };
        settings.Click += (_, _) => EditLauncher();
        menu.Items.Add(settings);
        var placement = new MenuItem
        {
            Header = "表示位置を設定…",
            IsEnabled = CanEditLauncher
        };
        placement.Click += (_, _) => EditLauncherPosition();
        menu.Items.Add(placement);
        var reload = new MenuItem { Header = "設定を再読込み", IsEnabled = CanEditLauncher };
        reload.Click += (_, _) => ReloadLauncher();
        menu.Items.Add(reload);
        if (launcherHotkeys?.PendingBindings.Length > 0)
        {
            var retry = new MenuItem
            {
                Header = "ホットキーを再回収",
                IsEnabled = CanEditLauncher
            };
            retry.Click += (_, _) => RetryLauncherHotkeys();
            menu.Items.Add(retry);
        }
        if (launcher.LoadError is not null)
        {
            var reset = new MenuItem { Header = "退避して初期化…", IsEnabled = CanEditLauncher };
            reset.Click += (_, _) => ResetLauncher();
            menu.Items.Add(reset);
        }

        launcherMenu = menu;
        if (position is { UseCursorPosition: false })
        {
            // 画面座標を表示単位へ変換し、画面端の補正はWPFのメニュー配置に任せます。
            Point point = PointFromScreen(new Point(position.X, position.Y));
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.RelativePoint;
            menu.HorizontalOffset = point.X;
            menu.VerticalOffset = point.Y;
        }
        else if (atPointer)
        {
            menu.Placement = PlacementMode.MousePoint;
        }
        else
        {
            menu.PlacementTarget = LauncherButton;
        }
        menu.IsOpen = true;

        void AddEntries(ItemCollection items, IEnumerable<LauncherEntry?> entries)
        {
            foreach (LauncherEntry? entry in entries)
            {
                if (entry is null)
                {
                    continue;
                }

                // タイトル内の下線をアクセスキーとして解釈させない。
                var item = new MenuItem { Header = new TextBlock { Text = entry.Title } };
                if (entry is LauncherGroup group)
                {
                    AddEntries(item.Items, group.Slots);
                    item.IsEnabled = item.Items.Count != 0;
                }
                else if (entry is LauncherCommand command)
                {
                    item.ToolTip = command.Path;
                    item.Click += async (_, _) => await LaunchFromMenuAsync(command);
                }

                items.Add(item);
            }
        }
    }

    private void EditLauncherPosition()
    {
        if (!CanEditLauncher)
        {
            return;
        }

        launcherMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var dialog = new LauncherPositionWindow(launcherPositionSettingsPath)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private async Task OpenLauncherFromHotkeyAsync(int id)
    {
        try
        {
            ApplicationLauncherSettings position =
                await ApplicationLauncherSettings.LoadAsync(launcherPositionSettingsPath);
            // 読込み待機中のキー変更・モーダル表示・終了を反映します。
            if (!launcherClosed && Native.IsWindowEnabled(windowHandle)
                && launcherHotkeys?.Accepts(id) == true)
            {
                OpenLauncher(atPointer: true, position);
            }
        }
        catch (Exception error) when (IsLauncherError(error))
        {
            ReportLauncherError(error);
        }
    }

    private void EditLauncher()
    {
        if (!CanEditLauncher || launcher.LoadError is not null
            || launcher.Current is not { } current)
        {
            return;
        }

        launcherMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var dialog = new LauncherSettingsWindow(current, AppContext.BaseDirectory,
            candidate =>
            {
                try
                {
                    launcherHotkeys!.Apply(candidate, MainShortcuts(),
                        () => launcher.Save(candidate));
                    LauncherNotice.Text = "";
                }
                finally
                {
                    RefreshLauncherHotkeyNotice();
                }
            })
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void ResetLauncher()
    {
        if (!CanEditLauncher || !(confirmLauncherReset?.Invoke()
            ?? MessageBox.Show(this,
                "元の設定を別名で退避し、空の登録に初期化しますか？", "ランチの設定復旧",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes))
        {
            return;
        }

        try
        {
            string? backup = null;
            launcherHotkeys!.Apply(new LauncherSettings(), MainShortcuts(),
                () => backup = launcher.ResetWithBackup());
            LauncherNotice.Text = backup is null ? "空のランチ設定を作成しました。"
                : "元の設定を退避しました：" + backup;
        }
        catch (Exception error) when (IsLauncherError(error))
        {
            ReportLauncherError(error);
        }
        finally
        {
            RefreshLauncherHotkeyNotice();
        }
    }

    private IEnumerable<ScriptHotkey> MainShortcuts() => hotkeySettings.Enabled
        ? hotkeySettings.Keys.Select(key => new ScriptHotkey(key, (int)hotkeySettings.Modifiers))
        : [];

    private void RefreshLauncherHotkeyNotice()
    {
        LauncherHotkeyNotice.Text = launcherHotkeys is null ? "" : launcherHotkeys.Status;
        if (launcherHotkeys is not null)
        {
            if (launcherHotkeys.ActiveShortcut is ScriptHotkey active)
            {
                LauncherHotkeyNotice.Text += " " + LauncherSettingsWindow.DescribeShortcut(active);
            }

            foreach (LauncherHotkeyBinding binding in launcherHotkeys.PendingBindings)
            {
                LauncherHotkeyNotice.Text += Environment.NewLine
                    + "未回収: " + LauncherSettingsWindow.DescribeShortcut(binding.Shortcut);
            }
        }
        LauncherHotkeyNotice.Visibility = launcherHotkeys is null
            || launcherHotkeys.Status == "ランチ用ホットキーは未設定です。"
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RetryLauncherHotkeys()
    {
        if (!CanEditLauncher || launcherHotkeys is null)
        {
            return;
        }

        try
        {
            launcherHotkeys.RetryCleanup();
        }
        finally
        {
            RefreshLauncherHotkeyNotice();
        }
    }

    private Task SendWithLauncherHotkeysAsync(int key, Func<Task> send) =>
        launcherHotkeys?.SendKeyboardInputAsync(key, send) ?? send();

    private async Task<int?> LaunchFromMenuAsync(LauncherCommand command)
    {
        if (commandLine || !IsEnabled)
        {
            return null;
        }

        try
        {
            if (launchCommandOverride is not null)
            {
                launchCommandOverride(command);
                return null;
            }
            else
            {
                return await Task.Run(() => LauncherProcessLauncher.Start(command, AppContext.BaseDirectory));
            }
        }
        catch (Exception error) when (IsLauncherError(error))
        {
            ReportLauncherError(error);
            return null;
        }
    }

    private void ReportLauncherError(Exception error)
    {
        if (launcherClosed)
        {
            return;
        }

        LauncherNotice.Text = error.Message;
        if (errorReporter is not null)
        {
            errorReporter(error);
        }
        else
        {
            MessageBox.Show(this, error.Message, "ランチ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsLauncherError(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or System.ComponentModel.Win32Exception
        or NotSupportedException;
}
