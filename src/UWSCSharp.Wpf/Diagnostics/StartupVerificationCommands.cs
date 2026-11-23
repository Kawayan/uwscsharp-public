using System.IO;
using System.Windows;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

public partial class App
{
    private static readonly Dictionary<string, VerificationCommand>
        VerificationCommands = new(StringComparer.Ordinal)
        {
            ["--verify-scheduler-history"] = WithReceiptFile(
                SchedulerHistoryVerification.RunAsync),
            ["--verify-scheduler-registration"] = WithReceiptFile(
                SchedulerRegistrationVerification.RunAsync),
            ["--verify-scheduler-storage"] = WithReceiptFile(
                SchedulerStorageVerification.RunAsync),
            ["--verify-scheduler-preparation-close"] = WithReceiptFile(
                SchedulerPreparationCloseVerification.RunAsync),
            ["--verify-scheduler-log"] = WithReceiptFile(
                SchedulerLogVerification.RunAsync),
            ["--verify-scheduler-lifetime"] = WithReceiptFile(
                SchedulerLifetimeVerification.RunAsync),
            ["--verify-scheduler-host"] = WithReceiptFile(
                SchedulerRuntimeHostVerification.RunAsync),
            ["--verify-scheduler-window"] = WithReceiptFile(
                SchedulerWindowVerification.RunAsync),
            ["--verify-scheduler-handoff-owner"] = WithReceiptFile(
                (directory, receipt) =>
                    SchedulerHandoffVerification.RunAsync(directory, receipt, true)),
            ["--verify-scheduler-handoff-successor"] = WithReceiptFile(
                (directory, receipt) =>
                    SchedulerHandoffVerification.RunAsync(directory, receipt, false)),
            ["--verify-scheduler-worker"] = WithReceiptFile(
                SchedulerWorkerVerification.RunAsync),
            ["--verify-scheduler-startup-fault"] = WithReceiptFile(
                SchedulerStartupFaultVerification.RunAsync),
            ["--verify-scheduler-worker-exit"] = WithReceiptFile(
                SchedulerWorkerExitVerification.RunAsync),
            ["--verify-scheduler-control-disconnect"] = WithReceiptFile(
                SchedulerControlDisconnectVerification.RunAsync),
            ["--verify-scheduler-runtime-disconnect"] = WithReceiptFile(
                SchedulerRuntimeDisconnectVerification.RunAsync),
            ["--verify-scheduler-ownership-owner"] = WithReceiptFile(
                (directory, receipt) =>
                    SchedulerOwnershipLossVerification.RunAsync(directory, receipt, true)),
            ["--verify-scheduler-ownership-successor"] = WithReceiptFile(
                (directory, receipt) =>
                    SchedulerOwnershipLossVerification.RunAsync(directory, receipt, false)),
            ["--verify-scheduler-overlap-owner"] = WithReceiptFile(
                (directory, receipt) =>
                    SchedulerOverlappingHandoffVerification.RunAsync(directory, receipt, true)),
            ["--verify-scheduler-overlap-successor"] = WithReceiptFile(
                (directory, receipt) =>
                    SchedulerOverlappingHandoffVerification.RunAsync(directory, receipt, false)),
            ["--verify-scheduler-identity"] = WithReceiptFile(
                SchedulerIdentityVerification.RunAsync),
            ["--verify-scheduler-identity-ui"] = WithReceiptFile(
                SchedulerIdentityUiVerification.RunAsync),
            ["--verify-scheduler-hotkey-restore"] = WithReceiptFile(
                SchedulerHotkeyRestoreVerification.RunAsync),
            ["--verify-scheduler-history-deadline"] = WithReceiptFile(
                SchedulerHistoryDeadlineVerification.RunAsync),
            ["--verify-scheduler-worker-log-denial"] = WithReceiptFile(
                SchedulerWorkerLogDenialVerification.RunAsync),
            ["--verify-scheduler-worker-history-denial"] = WithReceiptFile(
                SchedulerWorkerHistoryDenialVerification.RunAsync),
            ["--verify-scheduler-wake-shutdown"] = WithReceiptFile(
                SchedulerWakeShutdownVerification.RunAsync),
            ["--verify-scheduler-same-process"] = WithReceiptFile(
                SchedulerSameProcessVerification.RunAsync),
            ["--verify-launcher-activity"] = WithReceiptFile(
                (directory, receipt) =>
                    LauncherActivityVerification.RunAsync(directory, receipt,
                        LauncherCommandKind.Executable)),
            ["--verify-launcher-script-activity"] = WithReceiptFile(
                (directory, receipt) =>
                    LauncherActivityVerification.RunAsync(directory, receipt,
                        LauncherCommandKind.Script)),
            ["--verify-launcher-document-activity"] = WithReceiptFile(
                (directory, receipt) =>
                    LauncherActivityVerification.RunAsync(directory, receipt,
                        LauncherCommandKind.Document)),
            ["--verify-launcher-session"] = WithReceiptFile(
                LauncherSessionVerification.RunAsync),
            ["--verify-launcher-menu"] = WithReceiptFile(
                LauncherMenuVerification.RunAsync),
            ["--verify-launcher-hotkey-registration"] = WithReceiptFile(
                LauncherHotkeyRegistrationVerification.RunAsync),
            ["--verify-tray-launcher"] = WithReceiptFile(
                TrayLauncherVerification.RunAsync),
            ["--verify-tray-shell"] = WithReceiptFile(
                TrayShellInteractionVerification.RunAsync),
            ["--inspect-tray-layout"] = WithReceiptFile(
                (directory, receipt) => TrayShellLayoutVerification.RunAsync(
                    directory, receipt)),
            ["--inspect-tray-explorer-restart"] = WithReceiptFile(
                (directory, receipt) => TrayShellLayoutVerification.RunAsync(
                    directory, receipt, restartExplorer: true)),
            ["--inspect-tray-pointer"] = WithReceiptFile(
                TrayPointerInvestigation.RunAsync),
            ["--verify-launcher-process"] = WithReceiptFile(
                LauncherProcessVerification.RunAsync),
            ["--verify-input-cleanup-public"] = WithConsoleError(
                PublicInputCleanupVerification.RunAsync),
            ["--verify-input-cleanup-exit-dialog"] = WithConsoleError(
                PublicInputCleanupVerification.RunExitDialogAsync),
            ["--verify-selective-input-editor"] = WithConsoleError(
                SelectiveInputEditorVerification.RunAsync),
            ["--verify-script-hotkey-lifetime"] = WithConsoleError(
                ScriptHotkeyLifetimeVerification.RunAsync),
            ["--verify-com-event-playback"] = WithReceiptFile(
                ComEventPlaybackVerification.RunAsync),
            ["--verify-power-commands"] = WithReceiptFile(
                PowerCommandVerification.RunAsync),
            ["--verify-restart-cancellation"] = WithReceiptFile(
                UWSCSharp.Wpf.MainWindow.VerifyRestartCancellationAsync),
            ["--verify-restart-confirmation-message"] = WithReceiptFile(
                UWSCSharp.Wpf.MainWindow.VerifyRestartConfirmationMessageAsync),
            ["--verify-runtime"] = WithExitCode(
                RuntimeVerification.RunAsync),
            ["--verify-recording-os"] = WithExitCode(
                RecordingOsVerification.RunAsync),
            ["--verify-recording-timeout"] = WithExitCode(
                RecordingOsVerification.RunTimeoutAsync),
            ["--verify-stop-options"] = WithExitCode(
                StopOptionsVerification.RunAsync),
            ["--verify-script-hotkeys"] = WithExitCode(
                receipt => ScriptHotkeyVerification.RunAsync(receipt)),
            ["--verify-script-hotkey-pause"] = WithExitCode(
                receipt =>
                    ScriptHotkeyVerification.RunAsync(receipt, verifyPause: true)),
            ["--verify-script-drop"] = WithExitCode(
                ScriptDropVerification.RunAsync),
            ["--verify-script-drop-ole"] = WithExitCode(
                receipt => ScriptDropOleVerification.RunAsync(receipt)),
            ["--verify-tray"] = WithExitCode(
                TrayVerification.RunAsync),
            ["--verify-tray-exit-state"] = WithExitCode(
                TrayVerification.SaveExitStateAsync, writeErrorFile: true),
            ["--verify-background-recording"] = WithExitCode(
                receipt => BackgroundRecordingVerification.RunAsync(receipt),
                writeErrorFile: true),
            ["--verify-high-level-recording"] = WithExitCode(
                receipt => BackgroundRecordingVerification.RunAsync(
                    receipt, background: false), writeErrorFile: true),
            ["--verify-background-recording-after-restart"] = WithReceiptFile(
                BackgroundRecordingVerification.RunAfterRestartAsync),
            ["--verify-recording-clipboard"] = WithExitCode(
                RecordingClipboardVerification.RunAsync),
            ["--verify-recording-clipboard-lock"] = WithExitCode(
                RecordingClipboardVerification.RunLockAsync),
            ["--verify-association-registration"] = WithExitCode(
                receipt =>
                    Task.FromResult(AssociationRegistrationVerification.Run(receipt))),
            ["--verify-log-settings"] = WithExitCode(
                LogSettingsVerification.RunAsync, writeErrorFile: true),
            ["--verify-window-settings"] = WithExitCode(
                WindowSettingsVerification.RunAsync, writeErrorFile: true),
            ["--verify-window-position"] = WithExitCode(
                WindowPositionVerification.RunAsync),
            ["--verify-script-drop-ole-process"] = WithExitCode(
                receipt =>
                    ScriptDropOleVerification.RunAsync(receipt, separateProcess: true)),
            ["--verify-script-drop-source"] = WithExitCode(
                ScriptDropOleProcessVerification.RunSourceAsync),
            ["--verify-script-drop-explorer"] = WithExitCode(
                receipt => ScriptDropExplorerVerification.RunAsync(receipt)),
            ["--verify-script-drop-shortcuts-explorer"] = WithExitCode(
                receipt =>
                    ScriptDropExplorerVerification.RunAsync(receipt, shortcuts: true)),
        };

    private static VerificationCommand WithReceiptFile(
        Func<string, string, Task<int>> run)
        => new(3, args => run(args[1], args[2]), ErrorReceiptIndex: 2);

    private static VerificationCommand WithReceiptFile(
        Func<string, string, string, Task<int>> run)
        => new(4, args => run(args[1], args[2], args[3]), ErrorReceiptIndex: 2);

    private static VerificationCommand WithConsoleError(
        Func<string, Task<int>> run)
        => new(2, args => run(args[1]), ReportToConsole: true);

    private static VerificationCommand WithExitCode(
        Func<string, Task<int>> run, bool writeErrorFile = false)
        => new(2, args => run(args[1]), RejectInvalidArguments: true,
            ErrorReceiptIndex: writeErrorFile ? 1 : null);

    private static VerificationCommand WithExitCode(
        Func<string, string, Task<int>> run)
        => new(3, args => run(args[1], args[2]), RejectInvalidArguments: true);

    private async Task RunVerificationAsync(StartupEventArgs e,
        VerificationCommand verification)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        int result = 1;
        try
        {
            result = e.Args.Length == verification.ArgumentCount
                ? await verification.RunAsync(e.Args)
                : 2;
        }
        catch (Exception error)
        {
            // 無人検査の失敗時はダイアログを開きません。
            if (verification.ErrorReceiptIndex is int receiptIndex
                && e.Args[receiptIndex] is string receipt)
            {
                File.WriteAllText(receipt + ".error", error.ToString());
            }
            else if (verification.ReportToConsole)
            {
                ReportCommandLineError(error);
            }
        }
        finally
        {
            Shutdown(result);
        }
    }

    private readonly record struct VerificationCommand(
        int ArgumentCount, Func<string[], Task<int>> RunAsync,
        bool RejectInvalidArguments = false,
        int? ErrorReceiptIndex = null, bool ReportToConsole = false);
}
