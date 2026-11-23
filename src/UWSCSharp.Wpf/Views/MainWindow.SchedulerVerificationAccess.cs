using System.Windows;
using System.Windows.Controls;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// 予約の診断で使う画面の状態と操作を提供します。
    /// </summary>
    internal sealed class SchedulerVerificationAccess(MainWindow window)
    {
        internal MainWindow Window => window;

        internal SchedulerExecutionGate Gate => window.schedulerGate;

        internal bool CanStartInput => window.inputCleanup.CanStart;

        internal bool IsPlaybackPaused => window.scheduledPlayback!.IsPaused;

        internal string PlaybackStatus =>
            window.scheduledPlayback!.DisplayedStatus;

        internal string DraftText => window.Editor.Text;

        internal string OutputText => window.Output.Text;

        internal bool IsModified => window.modified;

        internal bool CanPlay => window.PlayButton.IsEnabled;

        internal bool CanRecord => window.RecordButton.IsEnabled;

        internal Exception? SettingsLoadError =>
            window.schedulerSettings!.LoadError;

        internal bool IsAbandoned =>
            AbandonedSchedulerHosts.Contains(window.schedulerHost!);

        internal bool IsStarting => window.schedulerStarting;

        internal bool IsCloseReady => window.schedulerCloseReady;

        internal SchedulerRuntimeHost? Host => window.schedulerHost;

        internal int ScheduledCount => window.schedulerGate.ScheduledCount;

        internal bool IsClosed => window.schedulerWindowClosed;

        internal bool IsClosing => window.schedulerClosing;

        internal bool IsExitHeld => window.schedulerExitHeld;

        internal bool CanEnterInteractive =>
            window.schedulerGate.CanEnterInteractive;

        internal bool CanToggleMonitor =>
            window.SchedulerMonitorButton.IsEnabled;

        internal SchedulerSettingsController? Settings =>
            window.schedulerSettings;

        internal ContextMenu SettingsMenu =>
            window.SchedulerSettingsButton.ContextMenu;

        internal string StatusText => window.SchedulerStatus.Text;

        internal void EditSettings() => window.EditScheduler();

        internal void ReloadSettings() =>
            window.SchedulerSettingsAction(window.schedulerSettings!.Reload);

        internal void RefreshStatus() => window.RefreshSchedulerStatus();

        internal void SetDraft(string source)
        {
            window.Editor.Text = source;
            window.modified = true;
        }

        internal void SetDraft(string source, string output)
        {
            window.Editor.Text = source;
            window.Output.Text = output;
            window.modified = true;
        }

        internal void SetDraft(string source, string output, string path,
            string fileLabel)
        {
            window.Editor.Text = source;
            window.Output.Text = output;
            window.scriptPath = path;
            window.FileLabel.Text = fileLabel;
            window.modified = true;
        }

        internal void DiscardDraft() => window.modified = false;

        internal void TogglePause() =>
            window.PauseClick(window, new RoutedEventArgs());

        internal void RequestPlaybackStop() =>
            window.scheduledPlayback?.RequestStop();

        internal bool ApplyParentHotkeys(HotkeySettings settings)
        {
            window.hotkeySettings = settings;
            return window.hotkeys?.Apply(settings) == true;
        }

        internal void SetParentHotkeySettings(HotkeySettings settings) =>
            window.hotkeySettings = settings;

        internal bool ReapplyParentHotkeys() =>
            window.hotkeys!.Apply(window.hotkeySettings);

        internal bool? TryReadParentHotkeyRegistration(int id) =>
            window.hotkeys?.IsRegistered(id);

        internal SchedulerHistoryRecord ReadHistoryRecord(Guid id) =>
            window.schedulerStorage!.ReadHistoryRecord(id);

        internal bool IsParentHotkeyRegistered(int id) =>
            window.hotkeys!.IsRegistered(id);

        internal Task RunWorkerKeyboardAsync(int key, Func<Task> operation,
            CancellationToken token) =>
            window.RunWorkerKeyboardAsync(key, operation, token);

        internal void AllowExitAndDiscard()
        {
            window.schedulerExitConfirmation = () => true;
            window.modified = false;
        }

        internal void RefreshInputState() =>
            window.RefreshInputCleanupControls();

        internal Task RestartMonitorAsync() =>
            window.StartSchedulerMonitorAsync();

        internal SchedulerRuntimeHost CreatePlaybackHost(
            string settings, string logs, string monitor,
            TimeProvider? clock = null, Func<TimeZoneInfo>? timeZone = null,
            Func<CancellationToken, Task>? preparationCheck = null,
            Func<CancellationToken,
                Task<IReadOnlyList<WindowsObservedWindow>>>? windowSource = null,
            Action? startSound = null)
            => new(window.Dispatcher, new(settings), new(),
                window.schedulerGate,
                async (dispatch, token, started) =>
                    (await window.RunScheduledPlaybackAsync(
                        dispatch, logs, token, started)).Result,
                Environment.ProcessPath!, logs, monitor, clock, timeZone,
                preparationCheck: preparationCheck,
                windowSource: windowSource, startSound: startSound);

        internal Task InitializeWorkerAsync(string settings, string monitor,
            string logs, TimeProvider clock)
            => InitializeAsync(settings, monitor, logs, clock,
                (_, _, _) => Task.FromException<SchedulerExecutionResult>(
                    new NotSupportedException()));

        internal Task InitializeAsync(string settings, string monitor,
            string logs, TimeProvider clock,
            Func<SchedulerDispatch, CancellationToken, Action,
                Task<SchedulerExecutionResult>> execute,
            Func<bool>? confirmUnrecoveredExit = null,
            SchedulerRuntimeDiagnostics? diagnostics = null,
            string? executable = null,
            Func<int, Func<Task>, CancellationToken, Task>? keyboardGuard = null)
            => window.InitializeSchedulerAsync(settings, monitor, logs,
                confirmUnrecoveredExit:
                    confirmUnrecoveredExit ?? (() => false),
                createHost: (controller, logDirectory) =>
                    new SchedulerRuntimeHost(window.Dispatcher, controller,
                        window.schedulerApplication, window.schedulerGate,
                        execute, executable ?? Environment.ProcessPath!,
                        logDirectory, monitor, clock, () => TimeZoneInfo.Utc,
                        keyboardGuard: keyboardGuard,
                        diagnostics: diagnostics));
    }
}
