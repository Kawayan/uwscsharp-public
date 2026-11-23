namespace UWSCSharp.Core;

/// <summary>
/// 子THREADとCALLが共有する停止設定を、実行全体で解放します。
/// </summary>
internal sealed class StopFormOptionsScope(IScriptHost host) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IScriptStopFormOptionsSession? session;
    private bool closed;

    internal bool Supported => host is IScriptStopFormOptionsHost target
        && target.SupportsStopFormOptions;

    internal async ValueTask ApplyAsync(ScriptStopFormOption option, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            token.ThrowIfCancellationRequested();
            if (host is not IScriptStopFormOptionsHost target || !target.SupportsStopFormOptions)
            {
                throw new NotSupportedException("この実行元は停止設定に対応していません。");
            }

            session ??= await target.CreateStopFormOptionsSessionAsync(token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            if (session is null)
            {
                throw new InvalidOperationException("停止設定のセッションがありません。");
            }

            token.ThrowIfCancellationRequested();
            await session.ApplyAsync(option, token).ConfigureAwait(WorkerThreadContext.IsCurrent);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 全要求の終了を待ち、取消済みでも元の設定を復元します。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
        try
        {
            if (closed)
            {
                return;
            }

            closed = true;
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(WorkerThreadContext.IsCurrent);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
