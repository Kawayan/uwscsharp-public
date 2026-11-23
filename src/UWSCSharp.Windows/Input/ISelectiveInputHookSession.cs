namespace UWSCSharp.Windows;

/// <summary>
/// 専用スレッドから呼ぶフックセッションの操作です。
/// </summary>
internal interface ISelectiveInputHookSession
{
    bool IsRegistered
    {
        get;
    }

    void Start();

    bool Pump();

    void Wait(AutoResetEvent work, int milliseconds);

    void Stop();

    /// <summary>
    /// 解除と緊急キーの解放待ちで共有する停止予算を渡します。
    /// </summary>
    void PrepareStop(TimeProvider clock, long startedAt)
    {
    }

    /// <summary>
    /// 実行全体の緊急停止状態を通知処理へ接続します。
    /// </summary>
    void ConfigureEmergency(SelectiveInputEmergencyStop emergency)
    {
    }

    /// <summary>
    /// 解除前に判定を止めます。入力判定を持たないセッションでは何もしません。
    /// </summary>
    void Suspend()
    {
    }

    /// <summary>
    /// 登録失敗時の初回回収を除いた、自動解除の残り回数を取り出します。
    /// </summary>
    int TakeStopAttempts() => 2;

    void RefreshTargets();
}
