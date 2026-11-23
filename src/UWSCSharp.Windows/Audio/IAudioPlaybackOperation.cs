namespace UWSCSharp.Windows;

/// <summary>
/// 起動・開始・終端・解放を別々に観測する再生処理です。
/// </summary>
internal interface IAudioPlaybackOperation : IAsyncDisposable
{
    /// <summary>
    /// 終端通知後の正常終了を確認し、取消や強制停止とは区別します。
    /// </summary>
    Task FinishAsync(CancellationToken token);

    AudioPlaybackSignals Signals
    {
        get;
    }
}

/// <summary>
/// 1回の再生処理が返す通知と解放完了です。
/// </summary>
internal sealed record AudioPlaybackSignals(Task<uint> Launched,
    Task<AudioHostMessage> Started, Task<AudioHostMessage> Completed, Task Closed);

/// <summary>
/// 要求の世代と、その要求だけに属する待機先です。
/// </summary>
internal sealed record AudioPlaybackTicket(long Generation, Task<uint> Launched,
    Task<AudioHostMessage> Started, Task<AudioHostMessage> Completed, Task Closed)
{
    internal object? OwnerIdentity
    {
        get; init;
    }
}
