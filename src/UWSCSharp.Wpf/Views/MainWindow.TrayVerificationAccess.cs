using System.Windows;
using System.Windows.Controls;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// トレイの診断で使う画面の状態と操作を提供します。
    /// </summary>
    internal sealed class TrayVerificationAccess(MainWindow window)
    {
        internal nint Handle => window.windowHandle;

        internal TrayIcon? Icon => window.trayIcon;

        internal string Source => window.Editor.Text;

        internal bool IsModified => window.modified;

        internal void SetSource(string source) => window.Editor.Text = source;

        internal void ClickTrayButton() => window.TrayButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));

        internal void Hide() => window.TrayClick(window, new RoutedEventArgs());

        internal void Restore() => window.RestoreFromTray();

        internal void Exit() => window.ExitFromTray();
    }
}
