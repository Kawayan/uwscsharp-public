namespace UWSCSharp.Wpf;

/// <summary>
/// 表示名や位置と、待機を無効にする実行定義の変更を区別します。
/// </summary>
internal static class SchedulerExecutionDefinition
{
    internal static bool Same(SchedulerEntry left, SchedulerEntry right) =>
        left.Path.Equals(right.Path, StringComparison.Ordinal)
        && left.Mode == right.Mode
        && left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal)
        && SameCondition(left.Condition, right.Condition);

    private static bool SameCondition(SchedulerCondition left, SchedulerCondition right) =>
        (left, right) switch
        {
            (SchedulerStartupCondition, SchedulerStartupCondition) => true,
            (SchedulerIntervalCondition a, SchedulerIntervalCondition b) =>
                a.Unit == b.Unit && a.Value == b.Value,
            (SchedulerTimeCondition a, SchedulerTimeCondition b) =>
                a.Time == b.Time && a.DayKind == b.DayKind
                && a.Days.Order().SequenceEqual(b.Days.Order()),
            (SchedulerExpressionCondition a, SchedulerExpressionCondition b) =>
                a.Source.Equals(b.Source, StringComparison.Ordinal),
            (SchedulerOneShotCondition a, SchedulerOneShotCondition b) =>
                a.Date == b.Date && a.Time == b.Time,
            (SchedulerWindowCondition a, SchedulerWindowCondition b) =>
                a.Title.Equals(b.Title, StringComparison.Ordinal)
                && a.ClassName.Equals(b.ClassName, StringComparison.Ordinal),
            _ => false
        };
}
