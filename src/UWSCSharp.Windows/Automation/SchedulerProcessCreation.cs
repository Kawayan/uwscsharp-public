namespace UWSCSharp.Windows;

/// <summary>
/// 作成後の検査で失敗しても、作成前の失敗と区別します。
/// </summary>
internal sealed class SchedulerProcessCreation
{
    internal uint ProcessId
    {
        get; private set;
    }

    internal bool Created => ProcessId != 0;

    internal void MarkCreated(uint processId) => ProcessId = processId;
}
