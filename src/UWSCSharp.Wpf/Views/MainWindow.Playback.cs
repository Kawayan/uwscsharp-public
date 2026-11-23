using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    internal async Task<int> RunCommandLineAsync(string source, ScriptRunOptions options)
    {
        Editor.Text = source;
        scriptPath = options.SourcePath;
        FileLabel.Text = scriptPath;
        modified = false;
        int result = await PlayAsync(options.Arguments);
        if (!inputCleanup.CanStart)
        {
            string diagnostic = InputCleanupExitDiagnostic();
            if (commandLine)
            {
                App.ReportCommandLineError(new IOException(diagnostic));
            }
            else
            {
                StatusLabel.Text = "入力の回収が必要です";
                AppendOutput(OutputText.Join([diagnostic]));
            }
            return result == 0 ? 1 : result;
        }

        return result;
    }

    private async void PlayClick(object sender, RoutedEventArgs e) => await PlayAsync([]);

    private async Task<int> PlayAsync(IReadOnlyList<string> arguments)
    {
        if (pendingRestart is not null || !inputCleanup.CanStart
            || cancellation is not null || recording.HasRecorder
            || recording.IsStarting || recording.IsStopping)
        {
            return 2;
        }

        schedulerGate.SetInputCleanupConfirmed(inputCleanup.CanStart);
        using IDisposable? activity = schedulerGate.TryEnterInteractive();
        if (activity is null)
        {
            return 2;
        }

        string source = Editor.Text;
        string workingDirectory = Environment.CurrentDirectory;
        string? restartScriptPath = scriptPath;
        ProcessStartInfo? restart = null;
        int exitCode;
        string path = scriptPath ?? Path.Combine(
            Environment.CurrentDirectory, "untitled.uws");
        using var running = new CancellationTokenSource();
        using var control = new ScriptPauseControl();
        BufferedOutputHost? output = null;
        output = new BufferedOutputHost(() => Dispatcher.BeginInvoke(
            DispatcherPriority.Background, () => FlushOutput(output!)));
        IScriptHost host = new LogOutputHost(this, output, commandLine, running.Token);
        pause = control;
        cancellation = running;
        SetRunningUi(true);
        Output.Clear();
        logHistory.Reset();
        logView = logView with
        {
            Visible = !commandLine
        };
        if (logWindow is not null)
        {
            logWindow.SetOutput("");
            logWindow.Hide();
        }
        StatusLabel.Text = "実行中";
        try
        {
            EnsureStopWindow().ShowForPlayback(path, PlaybackView);
            // UIスレッドを占有せず、既存エンジンの取消と解放を使用します。
            ScriptStartupDefaults startup = await ScriptStartupDefaults.LoadAsync(
                cancellationToken: running.Token);
            EnsureStopWindow().SetSize(startup.StopSize);
            ScriptLogDefaults defaults = startup.Log;
            logFont = defaults.Font;
            logWindow?.SetFont(logFont);
            exitCode = await Task.Run(() => ScriptLogHost.RunScriptWithDefaultsAsync(
                host, path, defaults,
                logging => new Interpreter(
                windows: windowsAutomationFactory?.Invoke(selectiveInputCleanup)
                    ?? WindowsAutomation.WithAsyncHostWindows((kind, token) => new ValueTask<nint>(
                    Dispatcher.InvokeAsync(() => GetHostWindowHandle(kind, output),
                        DispatcherPriority.Normal, token).Task),
                    SendWithoutKeyboardHotkeysAsync, new WindowsCommandOptions
                    {
                        CommandHost = WindowsCommandHost.ForCurrentProcess()
                    }, selectiveInputCleanup),
                files: ScriptFiles.ForScript(path)).RunWithExitCodeAsync(source,
                    logging, running.Token, new ScriptRunOptions(arguments)
                    {
                        PauseControl = control,
                        DefaultFont = startup.DefaultFont,
                        SourcePath = path,
                        PowerOperation = request =>
                        {
                            if (request.Command != PowerCommand.RestartApplication)
                            {
                                executeSystemPowerCommand(request);
                                return null;
                            }

                            ProcessStartInfo start = PrepareApplicationRestart(
                                request, restartScriptPath, arguments, workingDirectory);
                            return () => restart = start;
                        }
                    }), failure => Dispatcher.BeginInvoke(() =>
                    {
                        AppendOutput(OutputText.Join(["ログ保存: " + failure.Message]));
                        if (commandLine)
                        {
                            App.ReportCommandLineError(failure);
                        }
                    })));
            FlushOutput(output);
            StatusLabel.Text = exitCode == 0
                ? "実行が完了しました"
                : $"終了コード {exitCode} で実行が終了しました";
        }
        catch (OperationCanceledException error)
        {
            FlushOutput(output);
            StatusLabel.Text = "実行を停止しました";
            if (ScriptLogHost.HasAdditionalDiagnostics(error))
            {
                AppendOutput(OutputText.Join([
                    ScriptLogHost.FormatDiagnostic(error, "実行を停止しました。")
                ]));
                if (commandLine)
                {
                    App.ReportCommandLineError(error);
                }
            }

            return 130;
        }
        catch (Exception ex)
        {
            // エラー表示より前に、実行中の出力を欄へ反映します。
            FlushOutput(output);
            pauseTimer.Stop();
            PauseButton.IsEnabled = false;
            stopWindow?.FinishPlayback();
            ShowError(ex);
            return 1;
        }
        finally
        {
            await FinishInteractivePlaybackAsync(activity);
        }

        if (exitCode == 0 && restart is not null)
        {
            if (running.IsCancellationRequested || !inputCleanup.CanStart)
            {
                return running.IsCancellationRequested ? 130 : 1;
            }

            pendingRestart = restart;
            restartExitCode = 0;
            Close();
            return restartExitCode;
        }

        return exitCode;
    }

    private async Task FinishInteractivePlaybackAsync(IDisposable activity)
    {
        pause = null;
        stopWindow?.FinishPlayback();
        await RefreshInputCleanupAsync();
        cancellation = null;
        // 開始可否を表示へ反映する前に、今回の実行枠を返します。
        activity.Dispose();
        SetRunningUi(false);
    }

    private void SetRunningUi(bool running)
    {
        if (running)
        {
            pauseTimer.Start();
        }
        else
        {
            pauseTimer.Stop();
            PauseButton.Content = "Ⅱ 一時停止";
        }

        var state = PlaybackUiState.Create(
            running, recording.HasRecorder);
        RecordButton.IsEnabled = state.CanRecord;
        SettingsButton.IsEnabled = state.CanEditRecordingSettings;
        LogSettingsButton.IsEnabled = state.CanEditRecordingSettings;
        WindowSettingsButton.IsEnabled = state.CanEditRecordingSettings;
        AssociationButton.IsEnabled = state.CanEdit;
        HelpButton.IsEnabled = state.CanEdit;
        OmitIdleRecordingCheckBox.IsEnabled = state.CanEditRecordingSettings;
        RelativeMouseRecordingCheckBox.IsEnabled = state.CanEditRecordingSettings;
        HighLevelRecordingCheckBox.IsEnabled = state.CanEditRecordingSettings;
        BackgroundRecordingCheckBox.IsEnabled = state.CanEditRecordingSettings;
        CopyRecordingCheckBox.IsEnabled = state.CanEditRecordingSettings;
        PauseButton.IsEnabled = state.CanPause;
        LoadButton.IsEnabled = state.CanEdit;
        SaveButton.IsEnabled = state.CanEdit;
        PlayButton.IsEnabled = state.CanEdit;
        StopButton.IsEnabled = state.CanStop;
        Editor.IsReadOnly = state.EditorReadOnly;
        RefreshInputCleanupControls();
    }

    private async void StopClick(object sender, RoutedEventArgs e)
    {
        if (scheduledPlayback is not null)
        {
            scheduledPlayback.RequestStop();
            return;
        }

        if (recording.HasRecorder)
        {
            await FinishRecordingAsync();
            return;
        }

        RequestPlaybackStop();
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        if (scheduledPlayback is not null)
        {
            scheduledPlayback.TogglePause();
            return;
        }

        if (pause is null || cancellation?.IsCancellationRequested == true)
        {
            return;
        }

        if (pause.IsPauseRequested)
        {
            pause.Resume();
        }
        else
        {
            pause.Pause();
        }

        RefreshPlaybackControls();
    }
}
