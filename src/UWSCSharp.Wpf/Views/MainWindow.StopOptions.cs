using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private StopOptionsSession? activeStopOptions;

    private IScriptStopFormOptionsSession CreateStopOptions(CancellationToken generation)
    {
        generation.ThrowIfCancellationRequested();
        if (cancellation is null || cancellation.Token != generation || activeStopOptions is not null)
        {
            throw new InvalidOperationException("この実行の停止設定は開始できません。");
        }

        StopWindow window = EnsureStopWindow();
        var session = new StopOptionsSession(this, generation, window,
            window.IsTopMost(), hotkeys?.StopSuppressed ?? false);
        activeStopOptions = session;
        return session;
    }

    private void ApplyStopOption(StopOptionsSession session, ScriptStopFormOption option)
    {
        session.Generation.ThrowIfCancellationRequested();
        if (session.Closed || !ReferenceEquals(activeStopOptions, session)
            || cancellation is null || cancellation.Token != session.Generation)
        {
            throw new InvalidOperationException("終了した実行の停止設定は変更できません。");
        }

        if (option == ScriptStopFormOption.TopMost)
        {
            session.TopChanged = true;
            session.Window.SetTopMost(true);
        }
        else if (option == ScriptStopFormOption.DisableStopHotkey)
        {
            if (hotkeys is null)
            {
                throw new NotSupportedException("停止ホットキーが初期化されていません。");
            }

            session.HotkeyChanged = true;
            try
            {
                hotkeys.SetStopSuppressed(true);
            }
            finally
            {
                HotkeyLabel.Text = hotkeys.Status;
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(option));
        }
    }

    private void RestoreStopOptions(StopOptionsSession session)
    {
        if (session.Closed)
        {
            return;
        }

        session.Closed = true;
        try
        {
            if (!ReferenceEquals(activeStopOptions, session)
                || cancellation is null || cancellation.Token != session.Generation)
            {
                throw new InvalidOperationException("別の実行へ停止設定を復元することはできません。");
            }

            List<Exception> errors = [];
            if (session.HotkeyChanged)
            {
                Restore(() => (hotkeys ?? throw new InvalidOperationException(
                    "停止ホットキーがありません。")).SetStopSuppressed(session.PreviousSuppression));
            }
            if (session.TopChanged)
            {
                Restore(() => session.Window.SetTopMost(session.PreviousTopMost));
            }

            if (errors.Count == 1)
            {
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }
            if (errors.Count > 1)
            {
                throw new AggregateException("停止設定の復元に失敗しました。", errors);
            }

            void Restore(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(activeStopOptions, session))
            {
                activeStopOptions = null;
            }
            if (hotkeys is not null)
            {
                HotkeyLabel.Text = hotkeys.Status;
            }
        }
    }

    private sealed class StopOptionsSession(MainWindow owner, CancellationToken generation,
        StopWindow window, bool topMost, bool suppressed) : IScriptStopFormOptionsSession
    {
        internal CancellationToken Generation => generation;

        internal StopWindow Window => window;

        internal bool PreviousTopMost => topMost;

        internal bool PreviousSuppression => suppressed;

        internal bool TopChanged;
        internal bool HotkeyChanged;
        internal bool Closed;
        private readonly object gate = new();
        private Task? disposal;

        public ValueTask ApplyAsync(ScriptStopFormOption option, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposal is not null, this);
                return new(owner.RunKeyboardOperationAsync(() =>
                    owner.Dispatcher.InvokeAsync(() => owner.ApplyStopOption(this, option),
                        DispatcherPriority.Normal, cancellationToken).Task));
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                disposal ??= owner.RunKeyboardOperationAsync(() =>
                    owner.Dispatcher.InvokeAsync(() => owner.RestoreStopOptions(this),
                        DispatcherPriority.Normal).Task);
                return new(disposal);
            }
        }
    }
}
