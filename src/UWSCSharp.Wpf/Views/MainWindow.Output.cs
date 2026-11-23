using System.Windows.Interop;
using System.Windows.Threading;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private void FlushOutput(BufferedOutputHost output)
    {
        IReadOnlyList<string> lines = output.Drain();
        if (lines.Count > 0)
        {
            AppendOutput(OutputText.Join(lines));
            foreach (string line in lines)
            {
                logHistory.Append(line);
            }

            UpdateLogWindow(logView.Visible && logHistory.Count > 0);
        }
    }

    private void UpdateLogWindow(bool show)
    {
        bool created = false;
        if (show && logWindow is null)
        {
            logWindow = CreateLogWindow();
            created = true;
        }

        if (logWindow is not null)
        {
            logWindow.SetOutput(logHistory.Render());
            if (created)
            {
                logWindow.Apply(logView);
                logView = new ScriptLogView(logView.Visible);
            }
            else if (show && !logWindow.IsVisible)
            {
                logWindow.Apply(new ScriptLogView(true));
            }
        }
    }

    private LogWindow CreateLogWindow()
    {
        var window = new LogWindow { Owner = this };
        window.SetFont(logFont);
        window.HiddenByUser += (_, _) => logView = logView with { Visible = false };
        return window;
    }

    private nint GetHostWindowHandle(WindowsHostWindow kind, BufferedOutputHost output) => kind switch
    {
        WindowsHostWindow.Main => new WindowInteropHelper(this).EnsureHandle(),
        WindowsHostWindow.Stop => GetStopWindowHandle(),
        WindowsHostWindow.Log => GetLogWindowHandle(output),
        _ => 0
    };

    private nint GetLogWindowHandle(BufferedOutputHost output)
    {
        FlushOutput(output);
        if (logWindow is null)
        {
            logWindow = CreateLogWindow();
            logWindow.SetOutput(logHistory.Render());
            logWindow.Apply(logView with
            {
                Visible = false
            });
            logView = new ScriptLogView(logView.Visible);
        }

        return new WindowInteropHelper(logWindow).EnsureHandle();
    }

    private void SetLogView(BufferedOutputHost output, ScriptLogView request)
    {
        FlushOutput(output);
        if (logWindow is null)
        {
            logView = new ScriptLogView(request.Visible, request.X ?? logView.X,
                request.Y ?? logView.Y, request.Width ?? logView.Width,
                request.Height ?? logView.Height);
            UpdateLogWindow(request.Visible);
        }
        else
        {
            logView = new ScriptLogView(request.Visible);
            logWindow.SetOutput(logHistory.Render());
            logWindow.Apply(request);
        }
    }

    private sealed class LogOutputHost(MainWindow owner, BufferedOutputHost output,
        bool commandLine, CancellationToken generation) : IScriptLogViewHost,
        IScriptStopFormOptionsHost, IScriptWindowPositionHost
    {
        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (commandLine)
            {
                Console.WriteLine(text);
            }

            return output.WriteOutputAsync(text, token);
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) =>
            output.DelayAsync(duration, token);

        public ValueTask SetWindowPositionAsync(ScriptWindowPosition request,
            CancellationToken token) => new(owner.Dispatcher.InvokeAsync(() =>
            {
                generation.ThrowIfCancellationRequested();
                if (owner.cancellation is null || owner.cancellation.Token != generation)
                {
                    throw new InvalidOperationException("終了した実行の画面は移動できません。");
                }

                // 出力のflushによる表示切替を避け、配置だけを変更します。
                if (owner.logWindow is null)
                {
                    owner.logWindow = owner.CreateLogWindow();
                    owner.logWindow.Apply(owner.logView with
                    {
                        Visible = false
                    });
                }
                owner.logWindow.PositionBesideStop(owner.EnsureStopWindow().SetPosition(request));
                owner.logView = new ScriptLogView(owner.logView.Visible);
            }, DispatcherPriority.Normal, token).Task);

        public ValueTask SetStopFormAsync(ScriptStopForm request, CancellationToken token) =>
            new(owner.Dispatcher.InvokeAsync(() => owner.ApplyStopForm(request, generation),
                DispatcherPriority.Normal, token).Task);

        public ValueTask<IScriptStopFormOptionsSession> CreateStopFormOptionsSessionAsync(
            CancellationToken token) => new(owner.Dispatcher.InvokeAsync(
                () => owner.CreateStopOptions(generation), DispatcherPriority.Normal, token).Task);

        public ValueTask SetLogViewAsync(ScriptLogView view, CancellationToken token) =>
            new(owner.Dispatcher.InvokeAsync(() => owner.SetLogView(output, view),
                DispatcherPriority.Normal, token).Task);

        public ValueTask SetLogHistoryLimitAsync(int maximumLines, CancellationToken token) =>
            new(owner.Dispatcher.InvokeAsync(() =>
            {
                owner.FlushOutput(output);
                owner.logHistory.SetMaximumEntries(maximumLines);
                owner.UpdateLogWindow(show: false);
            }, DispatcherPriority.Normal, token).Task);
    }

    private void AppendOutput(string text)
    {
        Output.AppendText(text);
        // 長時間のPRINTで表示用メモリが増え続けないよう、古い行から削除します。
        int trim = OutputText.TrimCount(Output.Text);
        if (trim > 0)
        {
            Output.Text = Output.Text[trim..];
        }

        Output.ScrollToEnd();
    }
}
