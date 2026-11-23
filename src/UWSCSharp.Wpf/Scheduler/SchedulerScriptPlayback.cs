using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UWSCSharp.Core;
using UWSCSharp.IO;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal sealed record SchedulerScriptPlaybackResult(int ExitCode,
    string? Diagnostic, bool CancellationCompleted);

/// <summary>
/// 編集欄から独立した、予約1実行の表示と資源を所有します。
/// </summary>
internal sealed class SchedulerScriptPlayback : IScriptLogViewHost,
    IScriptStopFormOptionsHost, IScriptWindowPositionHost, IDisposable
{
    private static readonly TimeSpan PlaybackViewUpdateInterval = TimeSpan.FromMilliseconds(100);

    private readonly SchedulerPlaybackHost owner;
    private readonly string path;
    private readonly IReadOnlyList<string> arguments;
    private readonly string logPath;
    private readonly IScriptLogStoragePolicy storagePolicy;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ScriptPauseControl pause = new();
    private readonly StopWindow stop = new();
    private readonly Window host = new()
    {
        Title = "UWSC# 予約実行",
        ShowInTaskbar = false,
        ShowActivated = false,
        Width = 1,
        Height = 1,
        WindowStyle = WindowStyle.None
    };
    private LogWindow? log;
    private PlaybackOptions? options;
    private bool running;
    private bool disposed;
    private string? loggingDiagnostic;
    private int updateRequested;
    private readonly DispatcherTimer viewTimer = new()
    {
        Interval = PlaybackViewUpdateInterval
    };

    internal SchedulerScriptPlayback(SchedulerPlaybackHost owner,
        string path, IReadOnlyList<string> arguments, string logPath,
        IScriptLogStoragePolicy storagePolicy,
        SchedulerWindowTrigger? windowTrigger = null)
    {
        this.owner = owner;
        this.path = path;
        this.arguments = Array.AsReadOnly(arguments.ToArray());
        this.logPath = logPath;
        this.storagePolicy = storagePolicy;
        WindowTrigger = windowTrigger;
        host.Owner = owner.Window;
        stop.StopRequested += (_, _) => RequestStop();
        stop.PauseRequested += (_, _) => TogglePause();
        viewTimer.Tick += (_, _) =>
        {
            if (running && pause.IsPauseRequested)
            {
                stop.Apply(View);
                owner.RefreshControls();
            }
        };
    }

    internal SchedulerOutputBuffer Output { get; } = new();

    internal SchedulerWindowTrigger? WindowTrigger
    {
        get;
    }

    internal bool IsPaused => pause.IsPaused;

    internal CancellationToken CancellationToken => cancellation.Token;

    internal string DisplayedStatus => stop.PlaybackStatus.Text;

    internal PlaybackControlView View => PlaybackControlView.Create(
        running, cancellation.IsCancellationRequested,
        pause.IsPauseRequested, pause.IsPaused);

    internal async Task<SchedulerScriptPlaybackResult> RunAsync()
    {
        running = true;
        viewTimer.Start();
        owner.RefreshControls();
        stop.ShowForPlayback(path, View);
        try
        {
            int code = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                string source = Source.ReadFile(path);
                return RunWithLogAsync(source);
            });
            return new(code, loggingDiagnostic, false);
        }
        catch (OperationCanceledException error)
        {
            return new(130, CombineLoggingDiagnostic(ScriptLogHost.FormatDiagnostic(error,
                "予約の実行を停止しました。")), true);
        }
        catch (Exception error)
        {
            string diagnostic = ScriptLogHost.FormatDiagnostic(error);
            Output.Append(diagnostic);
            return new(1, CombineLoggingDiagnostic(diagnostic), false);
        }
        finally
        {
            running = false;
            viewTimer.Stop();
            stop.FinishPlayback();
        }
    }

    private async Task<int> RunWithLogAsync(string source)
    {
        var logging = new ScriptLogHost(this, logPath,
            new ScriptLogByteLimit(4 * 1024 * 1024), storagePolicy,
            replaceInvalidText: true);
        try
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            }
            catch (Exception error)
            {
                ReportLogError(error);
            }
            return await logging.RunWithPeriodicFlushAsync(
                logHost => RunInterpreterAsync(source, logHost), ReportLogError);
        }
        finally
        {
            if (logging.LastStorageOmission is { } omission)
            {
                ReportLogError(omission);
            }
            if (logging.OmittedDefaultBytes > 0)
            {
                string message = $"予約ログの容量制限: UTF-8 {logging.OmittedDefaultBytes}バイトを省略しました。";
                loggingDiagnostic = loggingDiagnostic is null ? message
                    : loggingDiagnostic + Environment.NewLine + message;
                Output.Append(message);
            }
        }

        void ReportLogError(Exception error)
        {
            string message = "予約ログの保存: " + ScriptLogHost.FormatDiagnostic(error);
            if (loggingDiagnostic != message)
            {
                loggingDiagnostic = message;
                Output.Append(message);
            }
        }
    }

    private async Task<int> RunInterpreterAsync(string source, IScriptHost logHost)
    {
        ScriptStartupDefaults startup = await ScriptStartupDefaults.LoadAsync(
            cancellationToken: cancellation.Token);
        await owner.Dispatcher.InvokeAsync(() => stop.SetSize(startup.StopSize),
            DispatcherPriority.Normal, cancellation.Token);
        return await new Interpreter(windows: WindowsAutomation.WithAsyncHostWindows(
            (kind, token) => new ValueTask<nint>(owner.Dispatcher.InvokeAsync(
                () => GetWindow(kind), DispatcherPriority.Normal, token).Task),
            owner.SendWithoutKeyboardHotkeysAsync,
            new WindowsCommandOptions
            {
                CommandHost = WindowsCommandHost.ForCurrentProcess()
            }, owner.Cleanup, CaptureForWindows()), files: ScriptFiles.ForScript(path))
        .RunWithExitCodeAsync(source, logHost, cancellation.Token,
            new ScriptRunOptions(arguments)
            {
                PauseControl = pause,
                DefaultFont = startup.DefaultFont,
                SourcePath = path,
                PowerOperation = ExecutePowerCommand
            });
    }

    private Action? ExecutePowerCommand(PowerCommandRequest request)
    {
        if (request.Command == PowerCommand.RestartApplication)
        {
            throw new InvalidOperationException(
                $"{BuiltinFunctionNames.Poff}: 予約実行ではアプリを再起動できません。");
        }

        owner.ExecuteSystemPowerCommand(request);
        return null;
    }

    private WindowsCapturedWindow? CaptureForWindows()
    {
        if (WindowTrigger is not { } trigger
            || IntPtr.Size == 4 && trigger.Target.Handle > uint.MaxValue)
        {
            return null;
        }

        return new(unchecked((nint)trigger.Target.Handle),
            trigger.Target.ProcessId, trigger.Target.ThreadId);
    }

    private string CombineLoggingDiagnostic(string diagnostic) =>
        loggingDiagnostic is null ? diagnostic
            : diagnostic + Environment.NewLine + loggingDiagnostic;

    internal void RequestStop()
    {
        if (!disposed)
        {
            cancellation.Cancel();
            pause.Resume();
            stop.Apply(View);
            owner.RefreshControls();
        }
    }

    internal void TogglePause()
    {
        if (!running || cancellation.IsCancellationRequested)
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

        stop.Apply(View);
        owner.RefreshControls();
    }

    public ValueTask WriteOutputAsync(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Output.Append(text);
        if (Interlocked.Exchange(ref updateRequested, 1) == 0)
        {
            _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Volatile.Write(ref updateRequested, 0);
                if (!disposed)
                {
                    log?.SetOutput(Output.Render());
                }
            });
        }
        return ValueTask.CompletedTask;
    }

    public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
        Task.Delay(duration, token);

    public ValueTask SetLogViewAsync(ScriptLogView view, CancellationToken token) =>
        new(owner.Dispatcher.InvokeAsync(() => EnsureLog().Apply(view),
            DispatcherPriority.Normal, token).Task);

    public ValueTask SetLogHistoryLimitAsync(int maximumLines, CancellationToken token) =>
        new(owner.Dispatcher.InvokeAsync(() =>
        {
            Output.SetMaximumLines(maximumLines);
            log?.SetOutput(Output.Render());
        }, DispatcherPriority.Normal, token).Task);

    public ValueTask SetStopFormAsync(ScriptStopForm form, CancellationToken token) =>
        new(owner.Dispatcher.InvokeAsync(() => stop.ApplyRequest(form, View),
            DispatcherPriority.Normal, token).Task);

    public ValueTask SetWindowPositionAsync(ScriptWindowPosition request,
        CancellationToken token) => new(owner.Dispatcher.InvokeAsync(() =>
        {
            if (!running || disposed)
            {
                throw new InvalidOperationException("終了した予約の画面は移動できません。");
            }

            cancellation.Token.ThrowIfCancellationRequested();
            EnsureLog().PositionBesideStop(stop.SetPosition(request));
        }, DispatcherPriority.Normal, token).Task);

    public ValueTask<IScriptStopFormOptionsSession> CreateStopFormOptionsSessionAsync(
        CancellationToken token) => new(owner.Dispatcher.InvokeAsync(
            () => CreateOptions(), DispatcherPriority.Normal, token).Task);

    private IScriptStopFormOptionsSession CreateOptions()
    {
        if (!running || options is not null)
        {
            throw new InvalidOperationException("予約の停止設定を開始できません。");
        }

        options = new PlaybackOptions(this, stop.IsTopMost(),
            owner.Hotkeys?.StopSuppressed ?? false);
        return options;
    }

    private LogWindow EnsureLog()
    {
        log ??= new LogWindow { Owner = owner.Window };
        log.SetOutput(Output.Render());
        return log;
    }

    private nint GetWindow(WindowsHostWindow kind) => kind switch
    {
        WindowsHostWindow.Main => new WindowInteropHelper(host).EnsureHandle(),
        WindowsHostWindow.Stop => new WindowInteropHelper(stop).EnsureHandle(),
        WindowsHostWindow.Log => new WindowInteropHelper(EnsureLog()).EnsureHandle(),
        _ => 0
    };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        viewTimer.Stop();
        log?.CloseForOwner();
        stop.CloseForOwner();
        host.Close();
        pause.Dispose();
        cancellation.Dispose();
    }

    private sealed class PlaybackOptions(SchedulerScriptPlayback execution,
        bool previousTopmost, bool previousSuppression) : IScriptStopFormOptionsSession
    {
        private bool topChanged;
        private bool suppressionChanged;
        private bool restored;

        public ValueTask ApplyAsync(ScriptStopFormOption option, CancellationToken token) =>
            new(execution.owner.RunKeyboardOperationAsync(() =>
                execution.owner.Dispatcher.InvokeAsync(() =>
                {
                    ObjectDisposedException.ThrowIf(restored, this);
                    token.ThrowIfCancellationRequested();
                    if (!execution.running || execution.disposed)
                    {
                        throw new InvalidOperationException("終了した予約の停止設定は変更できません。");
                    }

                    if (option == ScriptStopFormOption.TopMost)
                    {
                        topChanged = true;
                        execution.stop.SetTopMost(true);
                    }
                    else if (option == ScriptStopFormOption.DisableStopHotkey)
                    {
                        HotkeyRegistration hotkeys = execution.owner.Hotkeys
                            ?? throw new NotSupportedException("停止ホットキーがありません。");
                        suppressionChanged = true;
                        hotkeys.SetStopSuppressed(true);
                    }
                    else
                    {
                        throw new ArgumentOutOfRangeException(nameof(option));
                    }
                }, DispatcherPriority.Normal, token).Task));

        public ValueTask DisposeAsync() => new(execution.owner.RunKeyboardOperationAsync(
            () => execution.owner.Dispatcher.InvokeAsync(() =>
            {
                if (restored)
                {
                    return;
                }

                restored = true;
                if (execution.disposed
                    || !execution.owner.IsCurrent(execution))
                {
                    throw new InvalidOperationException("終了した予約へ停止設定を復元できません。");
                }
                try
                {
                    if (suppressionChanged)
                    {
                        execution.owner.Hotkeys!.SetStopSuppressed(previousSuppression);
                    }
                }
                finally
                {
                    if (topChanged)
                    {
                        execution.stop.SetTopMost(previousTopmost);
                    }

                    execution.options = null;
                }
            }, DispatcherPriority.Normal).Task));
    }
}
