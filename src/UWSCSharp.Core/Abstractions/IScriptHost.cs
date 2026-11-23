namespace UWSCSharp.Core;
/// <summary>
/// CLI、GUI、テストが提供する外部への作用。コールバックはワーカースレッドで実行される場合があります。
/// </summary>
public interface IScriptHost
{
    ValueTask WriteOutputAsync(string text,
        CancellationToken cancellationToken);

    Task DelayAsync(TimeSpan duration,
        CancellationToken cancellationToken);
}

/// <summary>
/// CLIなど、コールバックを利用する呼び出し元向けの既定ホスト。
/// </summary>
public sealed class CallbackScriptHost(
    Action<string> output) : IScriptHost
{
    private readonly Action<string> output = output
        ?? throw new ArgumentNullException(nameof(output));

    /// <summary>
    /// スクリプトの出力をホストに渡します。
    /// </summary>
    public ValueTask WriteOutputAsync(string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        output(text);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// キャンセルに対応して指定時間待機します。
    /// </summary>
    public Task DelayAsync(TimeSpan duration,
        CancellationToken cancellationToken) => Task.Delay(
        duration, cancellationToken);
}

/// <summary>
/// 呼び出しごとに独立した変数の状態を持ちます。スケジュールとキャンセルは呼び出し元が管理します。
/// </summary>
public interface IScriptRunner
{
    /// <summary>
    /// 起動引数を指定して実行します。
    /// </summary>
    Task RunAsync(string source, IScriptHost host,
        CancellationToken cancellationToken, ScriptRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Arguments.Count == 0
            ? RunAsync(source, host, cancellationToken)
            : throw new NotSupportedException("このrunnerは起動引数に未対応です。");
    }

    /// <summary>
    /// 起動引数を指定してコンパイル済みプログラムを実行します。
    /// </summary>
    Task RunAsync(ScriptProgram program, IScriptHost host,
        CancellationToken cancellationToken, ScriptRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Arguments.Count == 0
            ? RunAsync(program, host, cancellationToken)
            : throw new NotSupportedException("このrunnerは起動引数に未対応です。");
    }

    Task RunAsync(string source, IScriptHost host,
        CancellationToken cancellationToken = default);

    Task RunAsync(ScriptProgram program, IScriptHost host,
        CancellationToken cancellationToken = default);
}
