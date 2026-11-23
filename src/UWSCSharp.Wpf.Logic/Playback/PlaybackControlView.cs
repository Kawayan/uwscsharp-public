namespace UWSCSharp.Wpf;

internal enum PlaybackControlPhase
{
    Idle,
    Running,
    PauseRequested,
    Paused,
    Stopping
}

/// <summary>
/// 本体と独立した操作画面で共有する再生状態です。
/// </summary>
internal readonly record struct PlaybackControlView(
    PlaybackControlPhase Phase, string Status, string PauseCaption)
{
    internal bool IsActive => Phase != PlaybackControlPhase.Idle;

    internal bool CanStop => IsActive
        && Phase != PlaybackControlPhase.Stopping;

    internal bool CanPause => CanStop;

    internal static PlaybackControlView Create(bool running,
        bool stopping, bool pauseRequested, bool paused)
    {
        if (!running)
        {
            return new(PlaybackControlPhase.Idle,
                "待機中", "Ⅱ 一時停止");
        }

        if (stopping)
        {
            return new(PlaybackControlPhase.Stopping,
                "停止を要求しました", "Ⅱ 一時停止");
        }

        if (pauseRequested)
        {
            return new(paused ? PlaybackControlPhase.Paused
                : PlaybackControlPhase.PauseRequested,
                paused ? "一時停止中" : "一時停止要求中", "▶ 再開");
        }

        return new(PlaybackControlPhase.Running,
            "実行中", "Ⅱ 一時停止");
    }
}
