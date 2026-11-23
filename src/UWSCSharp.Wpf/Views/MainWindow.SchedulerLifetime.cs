using System.Windows;
using System.Windows.Threading;
using UWSCSharp.IO;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private static readonly TimeSpan SchedulerStatusRefreshInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan SchedulerCloseTimeout = TimeSpan.FromSeconds(20);

    // 明示終了でも回収済みとはせず、OSによるプロセス終了まで所有を保持します。
    private static readonly List<SchedulerRuntimeHost> AbandonedSchedulerHosts = [];
    private readonly SchedulerApplicationSession schedulerApplication = new();
    private readonly DispatcherTimer schedulerStatusTimer = new()
    {
        Interval = SchedulerStatusRefreshInterval
    };
    private SchedulerSettingsController? schedulerSettings;
    private SchedulerManagedStorage? schedulerStorage;
    private SchedulerRuntimeHost? schedulerHost;
    private bool schedulerStarting;
    private bool schedulerClosing;
    private Exception? schedulerLifetimeError;
    private bool schedulerCloseReady;
    private bool schedulerExitHeld;
    private bool schedulerWindowClosed;
    private Func<bool>? schedulerExitConfirmation;

    private bool SchedulerLifetimeBusy => schedulerStarting || schedulerClosing;

    internal async Task InitializeSchedulerAsync(string? settingsPath = null,
        string? leasePath = null, string? logs = null,
        Func<bool>? confirmUnrecoveredExit = null,
        Func<int, Func<Task>, CancellationToken, Task>? keyboardGuard = null,
        Func<CancellationToken, Task>? preparationCheck = null,
        Func<SchedulerSettingsController, string, SchedulerRuntimeHost>? createHost = null)
    {
        Dispatcher.VerifyAccess();
        if (commandLine || schedulerHost is not null)
        {
            throw new InvalidOperationException("この画面では予約監視を初期化できません。");
        }
        schedulerExitConfirmation = confirmUnrecoveredExit;
        schedulerSettings = new SchedulerSettingsController(settingsPath,
            () => ApplicationScheduleSettings.LoadAsync().AsTask().GetAwaiter().GetResult().Capacity);
        string logDirectory = logs ?? ApplicationInfo.SettingsPath("SchedulerLogs");
        schedulerStorage = new SchedulerManagedStorage(logDirectory);
        schedulerHost = createHost?.Invoke(schedulerSettings, logDirectory)
            ?? new SchedulerRuntimeHost(Dispatcher, schedulerSettings,
            schedulerApplication, schedulerGate,
            async (dispatch, token, started) =>
                (await RunScheduledPlaybackAsync(dispatch, logDirectory, token, started)).Result,
            Environment.ProcessPath!, logDirectory, leasePath,
            keyboardGuard: keyboardGuard ?? RunWorkerKeyboardAsync,
            preparationCheck: preparationCheck);
        SchedulerPanel.Visibility = Visibility.Visible;
        schedulerStatusTimer.Tick += (_, _) => RefreshSchedulerStatus();
        schedulerStatusTimer.Start();
        Closed += (_, _) =>
        {
            schedulerWindowClosed = true;
            schedulerStatusTimer.Stop();
            if (schedulerHost.CanClose)
            {
                schedulerHost.Dispose();
            }
            else
            {
                AbandonedSchedulerHosts.Add(schedulerHost);
            }
        };
        await StartSchedulerMonitorAsync();
    }

    private async void SchedulerMonitorClick(object sender, RoutedEventArgs e) =>
        await StartSchedulerMonitorAsync();

    private async Task StartSchedulerMonitorAsync()
    {
        if (SchedulerLifetimeBusy || schedulerWindowClosed || schedulerHost is null)
        {
            return;
        }
        schedulerCloseReady = false;
        schedulerExitHeld = false;
        schedulerStarting = true;
        schedulerLifetimeError = null;
        try
        {
            schedulerSettings!.Reload();
            if (schedulerSettings.CanAcceptNewReservations)
            {
                await schedulerHost.StartAsync();
            }
        }
        catch (Exception error)
        {
            schedulerLifetimeError = error;
        }
        finally
        {
            schedulerStarting = false;
            RefreshSchedulerStatus();
        }
    }

    private async Task PrepareSchedulerCloseAsync()
    {
        if (schedulerClosing || schedulerHost is null)
        {
            return;
        }
        if (schedulerExitHeld && !schedulerHost.CanClose)
        {
            bool selected = schedulerExitConfirmation?.Invoke()
                ?? MessageBox.Show(this,
                    "予約の実行または入力資源の回収が完了していません。\n"
                    + "回収成功とは記録せず、本体を終了しますか？",
                    ApplicationInfo.Title, MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (selected)
            {
                schedulerCloseReady = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            return;
        }
        schedulerClosing = true;
        RefreshSchedulerStatus();
        try
        {
            schedulerCloseReady = await schedulerHost.StopAsync(SchedulerCloseTimeout);
            schedulerExitHeld = !schedulerCloseReady;
            if (schedulerCloseReady)
            {
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        catch (Exception error)
        {
            schedulerLifetimeError = error;
            schedulerExitHeld = true;
        }
        finally
        {
            schedulerClosing = false;
            RefreshSchedulerStatus();
        }
    }

    private void RefreshSchedulerStatus()
    {
        if (schedulerWindowClosed || schedulerHost is null)
        {
            return;
        }
        SchedulerMonitorButton.IsEnabled = !SchedulerLifetimeBusy
            && !schedulerHost.IsActive && schedulerHost.ActiveCount == 0;
        SchedulerSettingsButton.IsEnabled = CanEditScheduler;
        SchedulerStatus.Text = SchedulerLifetimeBusy ? "予約監視: 開始・終了を処理中"
            : schedulerExitHeld ? "予約の回収は未確認です。もう一度閉じると終了方法を確認します"
            : schedulerLifetimeError is not null ? "予約監視: " + schedulerLifetimeError.Message
            : schedulerHost.LastError is not null ? "予約監視: " + schedulerHost.LastError.Message
            : schedulerHost.WindowObservationError is not null ? "窓の観測診断: " + schedulerHost.WindowObservationError.Message
            : schedulerHost.NotificationError is not null ? "予約開始音の診断: " + schedulerHost.NotificationError.Message
            : schedulerHost.WakeError is not null ? "予約の自動復帰診断: " + schedulerHost.WakeError.Message
            : schedulerHost.PersistenceError is not null ? "予約履歴の保存診断: " + schedulerHost.PersistenceError.Message
            : schedulerSettings?.LoadError is not null ? "予約設定を読み込めません: " + schedulerSettings.LoadError.Message
            : schedulerHost.IsActive ? $"予約監視中（実行・回収待ち {schedulerHost.ActiveCount}件）"
            : schedulerHost.OwnsMonitor ? "予約監視: 回収待ち"
            : schedulerSettings?.CanAcceptNewReservations != true ? "予約監視: 無効"
            : "予約監視は停止中です。他の本体が監視中の場合は開始できません";
        RefreshInputCleanupControls();
    }
}
