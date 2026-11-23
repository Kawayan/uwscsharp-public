namespace UWSCSharp.Wpf;

/// <summary>
/// 内部検証で、実行の通信・監視所有権・復帰計画の同期点を受け取ります。
/// </summary>
internal sealed record SchedulerRuntimeDiagnostics(
    Action<SchedulerDispatch, Action>? WorkerControlConnected = null,
    Action<Action>? MonitorOwned = null,
    Action<SchedulerWakePlan>? WakePlanComputed = null);
