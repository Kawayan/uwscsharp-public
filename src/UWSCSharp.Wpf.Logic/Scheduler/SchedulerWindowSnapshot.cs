using System.IO;

namespace UWSCSharp.Wpf;

/// <summary>
/// 観測した窓と所有者の識別子です。観測間の全ての再生成は検出できません。
/// </summary>
internal readonly record struct SchedulerWindowTarget
{
    internal SchedulerWindowTarget(ulong handle, uint processId, uint threadId)
    {
        if (handle == 0 || processId == 0 || threadId == 0)
        {
            throw new InvalidDataException("観測した窓の識別子が不正です。");
        }

        Handle = handle;
        ProcessId = processId;
        ThreadId = threadId;
    }

    internal ulong Handle
    {
        get;
    }

    internal uint ProcessId
    {
        get;
    }

    internal uint ThreadId
    {
        get;
    }
}

internal sealed record SchedulerObservedWindow(
    SchedulerWindowTarget Target, string Title, string ClassName);

internal sealed record SchedulerWindowTrigger(
    SchedulerWindowTarget Target, DateTimeOffset ObservedAt);

/// <summary>
/// 完全な列挙と取得失敗を区別した、変更できない窓の観測です。
/// </summary>
internal sealed class SchedulerWindowSnapshot
{
    internal const int MaximumWindows = 4096;
    internal const int MaximumTitleLength = 32768;
    internal const int MaximumClassNameLength = 512;

    private SchedulerWindowSnapshot(DateTimeOffset observedAt,
        IReadOnlyList<SchedulerObservedWindow> windows, Exception? error)
    {
        ObservedAt = observedAt.ToUniversalTime();
        Windows = windows;
        Error = error;
    }

    internal DateTimeOffset ObservedAt
    {
        get;
    }

    internal IReadOnlyList<SchedulerObservedWindow> Windows
    {
        get;
    }

    internal Exception? Error
    {
        get;
    }

    internal static SchedulerWindowSnapshot Complete(DateTimeOffset observedAt,
        IEnumerable<SchedulerObservedWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var copy = new List<SchedulerObservedWindow>();
        var handles = new HashSet<ulong>();
        foreach (SchedulerObservedWindow window in windows)
        {
            if (window is null || copy.Count == MaximumWindows
                || window.Target.Handle == 0 || window.Target.ProcessId == 0
                || window.Target.ThreadId == 0 || !handles.Add(window.Target.Handle)
                || !ValidText(window.Title, MaximumTitleLength)
                || !ValidText(window.ClassName, MaximumClassNameLength))
            {
                throw new InvalidDataException("窓の観測が上限または形式に違反しています。");
            }

            copy.Add(window);
        }

        return new(observedAt, Array.AsReadOnly(copy.ToArray()), null);
    }

    internal static SchedulerWindowSnapshot Failed(
        DateTimeOffset observedAt, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(observedAt, [], error);
    }

    private static bool ValidText(string? value, int maximum) =>
        value is not null && value.Length <= maximum && !value.Contains('\0');
}
