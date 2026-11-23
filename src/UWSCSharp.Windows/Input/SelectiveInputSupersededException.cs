namespace UWSCSharp.Windows;

/// <summary>
/// 後続の明示解除によって登録要求が失効したことを示します。
/// </summary>
internal sealed class SelectiveInputSupersededException()
    : OperationCanceledException("後続の明示解除を優先します。");
