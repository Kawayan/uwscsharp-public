namespace UWSCSharp.Wpf;

/// <summary>
/// 再生中の画面操作の可否をまとめます。
/// </summary>
internal readonly record struct PlaybackUiState(
    bool CanRecord, bool CanEditRecordingSettings,
    bool CanPause, bool CanEdit, bool CanStop, bool EditorReadOnly)
{
    internal static PlaybackUiState Create(bool running, bool hasRecorder) =>
        new(!running, !running && !hasRecorder,
            running, !running, running, running);
}
