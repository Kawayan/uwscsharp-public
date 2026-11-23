using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private readonly Lazy<LocalHelp> help;
    private readonly Action<Exception> helpError;

    private static void OpenHelpBrowser(Uri address)
    {
        using var browser = Process.Start(new ProcessStartInfo
        {
            FileName = address.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private void HelpClick(object sender, RoutedEventArgs e)
    {
        if (!HelpButton.IsEnabled)
        {
            return;
        }

        try
        {
            help.Value.Open();
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or Win32Exception
            or ArgumentException or InvalidOperationException)
        {
            helpError(error);
        }
    }

    private void ShowHelpError(Exception error) => MessageBox.Show(this,
        "ヘルプを開けませんでした。\n" + error.Message,
        ApplicationInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);

    private void HelpPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = TryHelpKey(e.Key, Keyboard.Modifiers, e.IsRepeat)
            || e.Handled;
    }

    internal bool TryHelpKey(Key key, ModifierKeys modifiers, bool repeat)
    {
        if (key != Key.F1 || modifiers != ModifierKeys.None
            || !HelpButton.IsEnabled)
        {
            return false;
        }

        if (!repeat)
        {
            HelpClick(this, new RoutedEventArgs());
        }

        return true;
    }
}
