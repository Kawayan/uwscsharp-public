using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    private bool receivingScript;

    internal Func<string, DroppedScript> ResolveDroppedScript
    {
        get; init;
    } =
        new ScriptDropResolver().Resolve;

    private bool ScriptOperationActive => !inputCleanup.CanStart || cancellation is not null
        || recording.HasRecorder || recording.IsStarting || recording.IsStopping;

    private void ScriptDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (receivingScript || ScriptOperationActive
            || !e.AllowedEffects.HasFlag(DragDropEffects.Copy))
        {
            return;
        }

        try
        {
            if (ReadDroppedScript(e.Data) is not null)
            {
                e.Effects = DragDropEffects.Copy;
            }
        }
        catch (Exception error) when (error is ExternalException or ArgumentException)
        {
            // 移動中にデータを取得できなくても、ダイアログを繰り返し表示しません。
        }
    }

    private async void ScriptDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (receivingScript || ScriptOperationActive
            || !e.AllowedEffects.HasFlag(DragDropEffects.Copy))
        {
            return;
        }

        try
        {
            string? path = ReadDroppedScript(e.Data);
            if (path is null)
            {
                return;
            }

            // 元ファイルの移動・削除を送り側へ要求しません。
            e.Effects = DragDropEffects.Copy;
            await RunDroppedScriptAsync(path);
        }
        catch (Exception error) when (error is ExternalException or ArgumentException)
        {
            e.Effects = DragDropEffects.None;
            ShowError(error);
        }
    }

    private static string? ReadDroppedScript(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop, autoConvert: false))
        {
            return null;
        }

        // 原版で観測した順序に合わせ、先頭だけを使います。
        return data.GetData(DataFormats.FileDrop, autoConvert: false)
            is string[] { Length: > 0 } paths && !string.IsNullOrWhiteSpace(paths[0])
            ? paths[0] : null;
    }

    // 単一ファイルを受け取る内部入口です。ドロップの形式と件数はUI側で判定します。
    internal async Task<int?> RunDroppedScriptAsync(string path)
    {
        Dispatcher.VerifyAccess();
        if (receivingScript || ScriptOperationActive)
        {
            return null;
        }

        receivingScript = true;
        try
        {
            DroppedScript request;
            try
            {
                request = ResolveDroppedScript(path);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                ShowError(error);
                return 1;
            }
            if (!ConfirmDiscard() || ScriptOperationActive)
            {
                return null;
            }

            try
            {
                LoadScriptFile(Path.GetFullPath(request.Path));
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException or DecoderFallbackException
                or ArgumentException or NotSupportedException)
            {
                ShowError(error);
                return 1;
            }

            return await PlayAsync(request.Arguments);
        }
        finally
        {
            receivingScript = false;
        }
    }

    private void LoadScriptFile(string path)
    {
        string source = Source.ReadFile(path);
        Editor.Text = source;
        scriptPath = path;
        FileLabel.Text = path;
        modified = false;
        StatusLabel.Text = "読み込みました";
    }
}
