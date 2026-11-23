using System.Windows;
using System.Windows.Controls;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private SchedulerHistoryWindow? schedulerHistoryWindow;
    private bool CanEditScheduler => !commandLine && IsEnabled && !SchedulerLifetimeBusy
        && !schedulerWindowClosed && cancellation is null
        && !recording.HasRecorder && !recording.IsStarting && !recording.IsStopping;

    private void SchedulerSettingsClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditScheduler || schedulerSettings is null)
        {
            return;
        }
        var menu = new ContextMenu { PlacementTarget = SchedulerSettingsButton };
        Add("登録・再予約を編集…", EditScheduler, schedulerSettings.LoadError is null);
        Add("設定を再読込み", () => SchedulerSettingsAction(schedulerSettings.Reload));
        Add("実行履歴…", () =>
        {
            if (CanEditScheduler && schedulerStorage is not null)
            {
                if (schedulerHistoryWindow?.IsVisible == true)
                {
                    schedulerHistoryWindow.Activate();
                    return;
                }
                schedulerHistoryWindow = new SchedulerHistoryWindow(schedulerStorage) { Owner = this };
                schedulerHistoryWindow.Closed += (_, _) => schedulerHistoryWindow = null;
                schedulerHistoryWindow.Show();
            }
        });
        if (schedulerSettings.LoadError is not null)
        {
            Add("退避して初期化…", () =>
            {
                if (MessageBox.Show(this, "元の予約設定を退避し、空の登録へ初期化しますか？",
                    "予約設定の復旧", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) == MessageBoxResult.Yes)
                {
                    SchedulerSettingsAction(() => schedulerSettings.ResetWithBackup());
                }
            });
        }
        SchedulerSettingsButton.ContextMenu = menu;
        menu.IsOpen = true;

        void Add(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void EditScheduler() => SchedulerSettingsAction(() =>
    {
        schedulerSettings!.Reload();
        var dialog = new SchedulerSettingsWindow(schedulerSettings.BeginEdit(),
            AppContext.BaseDirectory, draft => schedulerSettings.CommitEdit(draft,
                DateTimeOffset.UtcNow, TimeZoneInfo.Local))
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            schedulerHost!.Poll();
        }
    });

    private void SchedulerSettingsAction(Action action)
    {
        try
        {
            action();
            schedulerLifetimeError = null;
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            schedulerLifetimeError = error;
        }
        RefreshSchedulerStatus();
    }
}
