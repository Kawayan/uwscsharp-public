using System.Windows;

namespace UWSCSharp.Wpf;

public partial class MainWindow
{
    /// <summary>
    /// ドロップ診断で使う画面の状態と操作を提供します。
    /// </summary>
    internal sealed class ScriptDropVerificationAccess(MainWindow window)
    {
        internal UIElement DropTarget => window.Editor;

        internal string Source => window.Editor.Text;

        internal string Output => window.Output.Text;

        internal string? ScriptPath => window.scriptPath;

        internal bool IsModified => window.modified;

        internal bool IsPlaying => window.cancellation is not null;

        internal bool IsReceiving => window.receivingScript;

        internal LogWindow? LogWindow => window.logWindow;

        internal void SetDraft(string source, string path)
        {
            window.Editor.Text = source;
            window.scriptPath = path;
        }

        internal void ExpandEditor() => window.EditorPanel.IsExpanded = true;

        internal void ClearOutput() => window.Output.Clear();

        internal void DiscardDraft() => window.modified = false;

        internal void CancelActivePlayback() => window.cancellation!.Cancel();

        internal void CancelPlayback() => window.cancellation?.Cancel();
    }
}
