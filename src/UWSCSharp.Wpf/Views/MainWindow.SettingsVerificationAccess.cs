using System.Windows;
using System.Windows.Controls;
using UWSCSharp.IO;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// 設定の診断で使う画面の状態と操作を提供します。
    /// </summary>
    internal sealed class SettingsVerificationAccess(MainWindow window)
    {
        internal string LogText => window.logHistory.Render();

        internal ScriptLogFont LogFont => window.logFont;

        internal ApplicationWindowSize? RequestedMainSize =>
            window.requestedMainSize;

        internal string StatusText => window.StatusLabel.Text;

        internal bool CanEditLogSettings => window.LogSettingsButton.IsEnabled;

        internal bool CanEditWindowSettings =>
            window.WindowSettingsButton.IsEnabled;

        internal double EditorBottom => window.Editor.TranslatePoint(
            new Point(0, window.Editor.ActualHeight), window).Y;

        internal void SetPanelsExpanded(bool expanded)
        {
            window.EditorPanel.IsExpanded = expanded;
            window.SchedulerPanel.Visibility = expanded
                ? Visibility.Visible : Visibility.Collapsed;
        }

        internal void SetPlayback(CancellationTokenSource? cancellation)
        {
            window.cancellation = cancellation;
            window.SetRunningUi(cancellation is not null);
        }

        internal StopWindow GetStopWindow() => window.EnsureStopWindow();

        internal void OpenLogSettings() => window.EditLogSettings();

        internal void OpenWindowSettings() =>
            window.WindowSettingsClick(window, new RoutedEventArgs());

        internal void ClickLogSettings() => window.LogSettingsButton
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        internal void ClickWindowSettings() => window.WindowSettingsButton
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        internal void CloseAfterPlayback()
        {
            window.cancellation = null;
            window.Close();
        }
    }
}
