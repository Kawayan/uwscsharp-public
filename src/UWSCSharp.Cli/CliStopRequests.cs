namespace UWSCSharp.Cli;

/// <summary>
/// 実行の取消と、終了後の回収待機からの退出を分けます。
/// </summary>
internal sealed class CliStopRequests(Action cancelScript)
{
    private int stage;

    internal bool ExitRequested => Volatile.Read(ref stage) == 2;

    internal void EnterRecovery() => Interlocked.CompareExchange(ref stage, 1, 0);

    internal void Finish() => Interlocked.Exchange(ref stage, 3);

    internal void Request()
    {
        while (true)
        {
            int current = Volatile.Read(ref stage);
            if (current == 0)
            {
                try
                {
                    cancelScript();
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref stage) == 3)
                {
                    // 終了確認と競合した遅い通知では、破棄済みの取消元を呼び直しません。
                }
                return;
            }

            if (current != 1 || Interlocked.CompareExchange(ref stage, 2, 1) == 1)
            {
                return;
            }
        }
    }
}
