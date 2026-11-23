using System.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private void LogSettingsClick(object sender, RoutedEventArgs e) => EditLogSettings();

    private void EditLogSettings(string? path = null)
    {
        if (cancellation is not null || recording.HasRecorder
            || recording.IsStarting || recording.IsStopping)
        {
            return;
        }

        var dialog = new LogSettingsWindow(path) { Owner = this };
        _ = dialog.ShowDialog();
    }
}
