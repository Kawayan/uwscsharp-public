using System.Windows;
using System.Windows.Interop;
using UWSCSharp.Core;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private StopWindow? stopWindow;

    private PlaybackControlView PlaybackView => PlaybackControlView.Create(
        cancellation is not null,
        cancellation?.IsCancellationRequested == true,
        pause?.IsPauseRequested == true,
        pause?.IsPaused == true);

    private StopWindow EnsureStopWindow()
    {
        if (stopWindow is null)
        {
            // 本体の最小化に従属させず、終了時は明示的に解放します。
            stopWindow = new StopWindow();
            stopWindow.StopRequested += (_, _) => RequestPlaybackStop();
            stopWindow.PauseRequested += (_, _) =>
                PauseClick(this, new RoutedEventArgs());
        }

        return stopWindow;
    }

    private void RefreshPlaybackControls()
    {
        PlaybackControlView view = PlaybackView;
        stopWindow?.Apply(view);
        PauseButton.Content = view.PauseCaption;
        PauseButton.IsEnabled = view.CanPause;
        StopButton.IsEnabled = view.CanStop;
        if (view.IsActive)
        {
            StatusLabel.Text = view.Status;
        }
    }

    private void RequestPlaybackStop()
    {
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        pause?.Resume();
        RefreshPlaybackControls();
    }

    private nint GetStopWindowHandle() =>
        new WindowInteropHelper(EnsureStopWindow()).EnsureHandle();

    private void ApplyStopForm(ScriptStopForm request, CancellationToken generation)
    {
        ArgumentNullException.ThrowIfNull(request);
        generation.ThrowIfCancellationRequested();
        if (cancellation is null || cancellation.Token != generation)
        {
            throw new InvalidOperationException("終了した実行の停止画面は変更できません。");
        }

        EnsureStopWindow().ApplyRequest(request, PlaybackView);
    }
}
