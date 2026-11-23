namespace UWSCSharp.Windows.Tests;

// 対象プロセスの応答に依存せず、ゲスト内の外側から所有Jobを期限付きで回収します。
internal sealed class SandboxProcessWatchdog : IAsyncDisposable
{
    private readonly CapturedProcess process;
    private readonly CancellationTokenSource stop = new();
    private readonly Task<bool> completion;
    private readonly Task<byte[]> output;

    private SandboxProcessWatchdog(CapturedProcess process, TimeSpan deadline)
    {
        this.process = process;
        output = process.ReadOutputAsync(64 * 1024);
        completion = WatchAsync(deadline);
    }

    internal uint ProcessId => process.Id;

    internal Task<bool> Completion => completion;

    internal Task<byte[]> Output => output;

    internal static async Task<SandboxProcessWatchdog> StartAsync(
        string executable, string commandLine, TimeSpan deadline)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("監視の検証はWindows Sandbox内で実行してください。");
        }

        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        CapturedProcess process = await CapturedProcess.StartAsync(executable, commandLine, killOnClose: true);
        return new SandboxProcessWatchdog(process, deadline);
    }

    private async Task<bool> WatchAsync(TimeSpan deadline)
    {
        bool expired = true;
        try
        {
            await Task.Delay(deadline, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            expired = false;
        }

        var failure = new TimeoutException("所有する検証プロセスを停止します。");
        await process.StopAfterFailureAsync(failure).ConfigureAwait(false);
        if (failure.Data.Count != 0)
        {
            throw failure;
        }

        await output.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return expired;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await stop.CancelAsync();
            await completion;
        }
        finally
        {
            process.Dispose();
            stop.Dispose();
        }
    }
}
