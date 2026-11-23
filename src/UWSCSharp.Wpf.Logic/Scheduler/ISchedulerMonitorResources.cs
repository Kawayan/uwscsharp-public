namespace UWSCSharp.Wpf;

/// <summary>
/// 監視所有権の内側で準備し、回収を確認してから解放する資源です。
/// </summary>
internal interface ISchedulerMonitorResources : IDisposable
{
    bool CanRelease
    {
        get;
    }

    Task PrepareAsync(CancellationToken cancellationToken);
}
