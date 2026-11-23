using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        modified = true;
    }

    private bool ConfirmDiscard()
    {
        if (!modified)
        {
            return true;
        }

        if (discardConfirmation is not null)
        {
            return discardConfirmation();
        }

        return MessageBox.Show(this,
            "保存していない変更を破棄しますか？", ApplicationInfo.Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes;
    }

    private void LoadClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "UWSCスクリプト (*.uws)|*.uws|すべてのファイル (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            LoadScriptFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException or DecoderFallbackException)
        {
            ShowError(ex);
        }
    }

    internal bool OpenInteractiveLaunch(string? path)
    {
        if (confirmingClose || SchedulerLifetimeBusy || !IsEnabled)
        {
            return false;
        }

        RestoreFromTray();
        if (path is null)
        {
            return true;
        }

        if (!IsEnabled || !LoadButton.IsEnabled || !ConfirmDiscard())
        {
            return false;
        }

        try
        {
            LoadScriptFile(path);
            return true;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or DecoderFallbackException
            or ArgumentException)
        {
            ShowError(error);
            return false;
        }
    }

    internal bool StartInteractiveLaunch(InteractiveInstance.RunRequest request)
    {
        if (!CanStartInteractiveLaunch() || !OpenInteractiveLaunch(request.Path)
            || !CanStartInteractiveLaunch())
        {
            return false;
        }

        _ = RunInteractiveLaunchAsync(request.Arguments);
        return true;
    }

    private bool CanStartInteractiveLaunch() => !ScriptOperationActive
        && pendingRestart is null && schedulerGate.CanEnterInteractive;

    private async Task RunInteractiveLaunchAsync(string[] arguments)
    {
        try
        {
            await PlayAsync(arguments);
        }
        catch (Exception error)
        {
            ShowError(error);
        }
    }

    private void SaveScriptFile(string path)
    {
        ScriptFileStore.Save(path, Editor.Text);
        scriptPath = path;
        FileLabel.Text = path;
        modified = false;
        StatusLabel.Text = "UTF-8（BOM付き）で保存しました";
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "UWSCスクリプト (*.uws)|*.uws",
            DefaultExt = ".uws",
            FileName = scriptPath ?? "新規スクリプト.uws",
            Title = "UTF-8（BOM付き）で保存"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SaveScriptFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException)
        {
            ShowError(ex);
        }
    }
}
