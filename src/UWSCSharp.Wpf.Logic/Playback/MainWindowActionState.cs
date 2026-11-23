namespace UWSCSharp.Wpf;

/// <summary>
/// 既存の実行・記録・回収状態から画面の操作可否を求めます。
/// </summary>
internal readonly record struct MainWindowActionState(
    bool CanPlay, bool CanRecord, bool CanRetryCleanup)
{
    internal static MainWindowActionState Create(
        bool running, bool recordingStarting, bool recordingStopping,
        bool sameProcessReserved, bool schedulerLifetimeBusy,
        bool hasRecorder, bool recordingCanRecord,
        bool canEnterInteractive, bool canRetryCleanup)
    {
        bool idle = !running && !recordingStarting && !recordingStopping
            && !sameProcessReserved;
        bool canStart = !schedulerLifetimeBusy && canEnterInteractive;
        return new(
            idle && !hasRecorder && canStart,
            idle && recordingCanRecord && (hasRecorder || canStart),
            idle && !hasRecorder && canRetryCleanup);
    }
}
