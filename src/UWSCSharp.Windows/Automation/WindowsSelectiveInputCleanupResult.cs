namespace UWSCSharp.Windows;

/// <summary>
/// 再回収の診断情報と、完了時点の未回収所有者数です。
/// </summary>
public sealed class WindowsSelectiveInputCleanupResult
{
    internal WindowsSelectiveInputCleanupResult(
        IReadOnlyList<Exception> errors, int pendingCount)
    {
        Errors = errors;
        PendingCount = pendingCount;
    }

    /// <summary>
    /// 過去の障害も含む診断一覧です。回収後も残る場合があります。
    /// </summary>
    public IReadOnlyList<Exception> Errors
    {
        get;
    }

    /// <summary>
    /// 要求完了時点の未回収所有者数です。後から変化する場合があります。
    /// </summary>
    public int PendingCount
    {
        get;
    }
}
