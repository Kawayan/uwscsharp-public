using System.IO;
using System.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private void AssociationClick(object sender, RoutedEventArgs e)
    {
        string executable = Path.Combine(AppContext.BaseDirectory,
            "UWSCSharp.Association.exe");
        var dialog = new AssociationSettingsWindow(executable) { Owner = this };
        dialog.ShowDialog();
    }
}
