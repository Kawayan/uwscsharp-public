namespace UWSCSharp.Core;

/// <summary>
/// ファイル関数の戻り値と、その呼出し直後の名前一覧です。
/// </summary>
internal readonly record struct ScriptFileCallResult(
    Value Value, IReadOnlyList<string> DirectoryFiles);

/// <summary>
/// 共有ファイルセッションの操作と結果の取得を直列化します。
/// </summary>
internal sealed class ConcurrentScriptFileSession(
    IScriptFileSession session)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    // 元のセッションは実行サービスが所有し、全workerの終了後に解放します。
    internal async ValueTask<ScriptFileCallResult> InvokeWithSnapshotAsync(
        string name, IReadOnlyList<Value> arguments,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value value = await session.InvokeAsync(name, arguments,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // 次の操作が一覧を更新する前に、呼出し結果と一緒にコピーします。
            // AsyncLocalの子から親への逆伝播には依存しません。
            IReadOnlyList<string> names = Array.AsReadOnly(
                session.DirectoryFiles.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            return new(value, names);
        }
        finally
        {
            gate.Release();
        }
    }
}
