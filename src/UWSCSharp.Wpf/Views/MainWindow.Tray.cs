using System.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private TrayIcon? trayIcon;
    private bool closingFromTray;
    private string? trayStartupStatePath;

    private void TrayClick(object sender, RoutedEventArgs e)
    {
        if (commandLine || !IsEnabled)
        {
            return;
        }

        StoreInTray();
    }

    internal void ShowAtStartup(bool startInTray, bool minimizeToTray = false,
        string? startupStatePath = null, bool restoreStartupState = false)
    {
        trayStartupStatePath = startupStatePath;
        if (minimizeToTray)
        {
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized && IsVisible && IsEnabled)
                {
                    StoreInTray();
                }
            };
        }

        if (startInTray || (restoreStartupState && startupStatePath is not null
            && System.IO.File.Exists(startupStatePath)))
        {
            // 初回表示の前にホットキーなどが使うハンドルを作ります。
            _ = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            if (StoreInTray())
            {
                return;
            }
        }

        Show();
    }

    private bool StoreInTray()
    {
        try
        {
            trayIcon ??= new TrayIcon(
                RestoreFromTray, ExitFromTray, () => OpenLauncher(atPointer: true));
            // 戻る手段を用意してからウィンドウを隠します。
            trayIcon.Visible = true;
            if (IsVisible)
            {
                Hide();
            }

            return true;
        }
        catch (Exception error)
        {
            ShowError(error);
            return false;
        }
    }

    internal void RestoreFromTray()
    {
        if (!IsEnabled)
        {
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
        }
    }

    private void ExitFromTray()
    {
        if (!IsEnabled)
        {
            return;
        }

        // 終了が取り消されても、確認や停止状態を画面で確認できます。
        RestoreFromTray();
        closingFromTray = true;
        Close();
    }

    private void SaveTrayStartupState()
    {
        if (trayStartupStatePath is not { } path)
        {
            return;
        }

        try
        {
            if (closingFromTray)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using System.IO.FileStream marker = System.IO.File.Open(path, System.IO.FileMode.OpenOrCreate,
                    System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
            }
            else
            {
                try
                {
                    System.IO.File.Delete(path);
                }
                catch (System.IO.DirectoryNotFoundException)
                {
                    // 設定フォルダーが未作成なら解除する状態はありません。
                }
            }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowError(error);
        }
    }
}
