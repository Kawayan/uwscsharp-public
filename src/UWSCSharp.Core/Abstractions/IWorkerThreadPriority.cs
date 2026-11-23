namespace UWSCSharp.Core;

/// <summary>
/// 所有Threadの優先度とnative資源の寿命を管理します。
/// </summary>
public interface IWorkerThreadPriority : IDisposable
{
    /// <summary>
    /// 実際に実行する所有Thread上で資源を取得します。
    /// </summary>
    void Initialize();

    /// <summary>
    /// 取得済みの所有Threadへ優先度を設定します。
    /// </summary>
    void SetPriority(WorkerThreadPriority priority);
}

/// <summary>
/// 原版の6段階に対応するWin32優先度値です。
/// </summary>
public enum WorkerThreadPriority
{
    /// <summary>
    /// アイドル優先度です。
    /// </summary>
    Idle = -15,
    /// <summary>
    /// 最低優先度です。
    /// </summary>
    Lowest = -2,
    /// <summary>
    /// 通常より低い優先度です。
    /// </summary>
    Lower = -1,
    /// <summary>
    /// 通常優先度です。
    /// </summary>
    Normal = 0,
    /// <summary>
    /// 通常より高い優先度です。
    /// </summary>
    Higher = 1,
    /// <summary>
    /// 最高優先度です。
    /// </summary>
    Highest = 2
}

/// <summary>
/// 原版CTRLTRDとTRD定数の制御値です。
/// </summary>
internal enum WorkerThreadCommand
{
    /// <summary>
    /// 文境界で停止します。
    /// </summary>
    Suspend = 1,
    /// <summary>
    /// 協調取消を要求します。
    /// </summary>
    Quit = 2,
    /// <summary>
    /// 停止中の文境界待機を解除します。
    /// </summary>
    Resume = 3,
    /// <summary>
    /// アイドル優先度へ変更します。
    /// </summary>
    Idle = 10,
    /// <summary>
    /// 最低優先度へ変更します。
    /// </summary>
    Lowest = 11,
    /// <summary>
    /// 通常より低い優先度へ変更します。
    /// </summary>
    Lower = 12,
    /// <summary>
    /// 通常優先度へ変更します。
    /// </summary>
    Normal = 13,
    /// <summary>
    /// 通常より高い優先度へ変更します。
    /// </summary>
    Higher = 14,
    /// <summary>
    /// 最高優先度へ変更します。
    /// </summary>
    Highest = 15
}
