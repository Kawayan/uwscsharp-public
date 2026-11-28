using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// スクリプトの編集と再生を提供します。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan RecordingIndicatorInterval = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan PauseIndicatorInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RecordingStopTimeout = TimeSpan.FromSeconds(3);

    private readonly bool commandLine;
    private readonly Func<WindowsSelectiveInputCleanup, IWindowsAutomation>? windowsAutomationFactory;
    private readonly OutputHistory logHistory = new();
    private LogWindow? logWindow;
    private ScriptLogFont logFont = new();
    private ScriptLogView logView = new(true);
    private string? scriptPath;
    private bool modified;
    private CancellationTokenSource? cancellation;
    private ScriptPauseControl? pause;
    private readonly RecordingController recording;
    private readonly Func<bool>? discardConfirmation;
    private readonly Action<Exception>? errorReporter;
    private readonly Action<PowerCommandRequest> executeSystemPowerCommand;
    private readonly DispatcherTimer pauseTimer = new()
    {
        Interval = PauseIndicatorInterval
    };
    private readonly DispatcherTimer recordTimer = new()
    {
        Interval = RecordingIndicatorInterval
    };
    private nint windowHandle;
    private HotkeyRegistration? hotkeys;
    private readonly SemaphoreSlim keyboardOperationGate = new(1, 1);
    private HotkeySettings hotkeySettings = new();
    private RecordingSettings recordingSettings = new();
    private readonly Action<string> copyRecordingText;
    private static string RecordingSettingsPath =>
        ApplicationInfo.SettingsPath("recording.json");

    private static string SettingsPath =>
        ApplicationInfo.SettingsPath("hotkeys.json");

    /// <summary>
    /// メイン画面を初期化します。
    /// </summary>
    public MainWindow() : this(commandLine: false)
    {
    }

    internal MainWindow(bool commandLine, Action<Uri>? openHelp = null,
        string? helpCacheRoot = null, Action<Exception>? reportHelpError = null,
        RecordingController? recordingController = null,
        Func<bool>? confirmDiscard = null, Action<Exception>? reportError = null,
        InputCleanupController? cleanupController = null,
        Func<bool>? confirmPendingCleanupExit = null,
        Func<WindowsSelectiveInputCleanup, IWindowsAutomation>? windowsAutomationFactory = null,
        Action<string>? copyRecordingText = null, string? launcherSettingsPath = null,
        Action<LauncherCommand>? launchCommand = null,
        Func<bool>? confirmLauncherReset = null,
        bool verifyRecordingInput = false,
        Action<PowerCommandRequest>? executeSystemPowerCommand = null)
    {
        this.commandLine = commandLine;
        this.windowsAutomationFactory = windowsAutomationFactory;
        this.copyRecordingText = copyRecordingText
            ?? (text => new ClipboardBackend().WriteText(text, default));
        discardConfirmation = confirmDiscard;
        errorReporter = reportError;
        this.executeSystemPowerCommand = executeSystemPowerCommand
            ?? WindowsPowerOperations.ExecuteSystemCommand;
        help = new(() => new LocalHelp(
            helpCacheRoot ?? ApplicationInfo.SettingsPath("Help"),
            openHelp ?? OpenHelpBrowser));
        helpError = reportHelpError ?? ShowHelpError;
        InitializeComponent();
        launcher = new LauncherSettingsController(launcherSettingsPath
            ?? ApplicationInfo.SettingsPath("launcher.json"));
        launchCommandOverride = launchCommand;
        this.confirmLauncherReset = confirmLauncherReset;
        if (verifyRecordingInput && !SandboxEnvironment.IsCurrent)
        {
            throw new InvalidOperationException("記録入力の検証はSandbox専用です。");
        }

        recording = recordingController ?? new RecordingController(() => new BasicRecordingDevice(
            new BasicRecorder(windowHandle,
                includeInjected: verifyRecordingInput,
                excludedKeys: hotkeys?.ActiveKeys ?? [],
                excludedModifiers: hotkeySettings.Modifiers,
                monitorHookHealth: true,
                additionalControls: launcherHotkeys?.RegisteredShortcuts ?? [])
            {
                OmitIdleTimeAndMouseMoves = recordingSettings.OmitIdleTimeAndMouseMoves,
                RelativeMouseCoordinates = recordingSettings.RelativeMouseCoordinates,
                RecordButtonClicks = recordingSettings.RecordButtonClicks,
                BackgroundPlayback = recordingSettings.BackgroundPlayback
            }), RecordingStopTimeout);
        inputCleanup = cleanupController ?? new InputCleanupController(
            () => selectiveInputCleanup.PendingCount, async () =>
            {
                WindowsSelectiveInputCleanupResult result = await selectiveInputCleanup.RetryAsync();
                return new InputCleanupSnapshot(result.Errors, result.PendingCount);
            });
        pendingCleanupExitConfirmation = confirmPendingCleanupExit;
        InitializeInputCleanup();
        SourceInitialized += InitializeHotkeys;
        SourceInitialized += InitializeRecordingSettings;
        SourceInitialized += InitializeLauncher;
        Closed += (_, _) =>
        {
            trayIcon?.Dispose();
            launcherClosed = true;
            launcherMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            trayIcon = null;
            recordTimer.Stop();
            hotkeys?.Dispose();
            logWindow?.CloseForOwner();
            stopWindow?.CloseForOwner();
        };
        recordTimer.Tick += async (_, _) =>
        {
            if (!recording.HasRecorder || recording.IsStarting || recording.IsStopping
                || !Native.IsWindowEnabled(windowHandle))
            {
                return;
            }

            if (!recording.IsRunning)
            {
                await FinishRecordingAsync(RecordingStopReason.WorkerStopped);
                return;
            }

            if (recording.RecoveryPending)
            {
                StatusLabel.Text = "記録の終了待ち：停止を再試行できます";
                return;
            }

            string state = recording.WaitingForInitialRelease
                ? "記録開始待ち：押下中のキー・ボタンを解放してください" : "記録中";
            StatusLabel.Text = string.IsNullOrEmpty(recording.Warning)
                ? state : state + "：" + recording.Warning;
        };
        pauseTimer.Tick += (_, _) =>
        {
            if (pause?.IsPauseRequested == true)
            {
                RefreshPlaybackControls();
            }
        };
    }

    internal void ReportAssociationError(Exception error)
    {
        ShowError(error);
    }

    private void ShowError(Exception ex)
    {
        StatusLabel.Text = "エラー";
        EditorPanel.IsExpanded = true;
        string message = ScriptLogHost.FormatDiagnostic(ex);
        AppendOutput(OutputText.Join([message]));
        if (errorReporter is not null)
        {
            errorReporter(ex);
            return;
        }

        if (commandLine)
        {
            App.ReportCommandLineError(ex);
            return;
        }

        MessageBox.Show(this, message, ApplicationInfo.Title,
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
