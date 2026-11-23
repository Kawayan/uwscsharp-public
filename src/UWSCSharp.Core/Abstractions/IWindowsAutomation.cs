namespace UWSCSharp.Core;
/// <summary>
/// スクリプトの実行ごとに独立した自動操作の状態を作成します。
/// </summary>
public interface IWindowsAutomation
{
    IWindowsAutomationSession CreateSession();
}

/// <summary>
/// ウィンドウID、座標原点、押下中の入力を管理します。Disposeで押下中の入力を解放します。
/// </summary>
public interface IWindowsAutomationSession : IAsyncDisposable
{
    /// <summary>
    /// 実行先から実行全体へ要求する協調停止のtokenです。既定では停止を要求しません。
    /// </summary>
    /// <remarks>実行中は同じtokenを返し、worker間で共有します。実行先が寿命を管理します。</remarks>
    CancellationToken ExecutionStopToken => CancellationToken.None;

    /// <summary>
    /// 実行全体が所有するホットキー通知元を作成します。
    /// </summary>
    IScriptHotkeySource CreateHotkeySource() =>
        throw new NotSupportedException("この実行先はホットキー登録に対応していません。");

    /// <summary>
    /// COMイベントの通知処理を登録します。callbackがnullなら対象の全登録を解除します。
    /// </summary>
    ValueTask SetComEventAsync(Value target, string? interfaceName,
        string? eventName, Func<IReadOnlyList<Value>, CancellationToken,
            ValueTask<IReadOnlyList<Value>>>? callback,
        CancellationToken cancellationToken) => throw new NotSupportedException(
            "この実行先はCOMイベントに対応していません。");

    /// <summary>
    /// 実行元の主スクリプト名をダイアログの既定タイトルへ渡します。
    /// </summary>
    void ConfigureDialogContext(string scriptName)
    {
    }

    /// <summary>
    /// 次の表示へ適用するダイアログ設定を変更します。
    /// </summary>
    void ConfigureDialogOption(string name, IReadOnlyList<Value> values,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("この実行先はダイアログ設定に対応していません。");

    /// <summary>
    /// 現在の物理座標または主画面の情報を読み取ります。
    /// </summary>
    Value ReadDisplayVariable(WindowsDisplayVariable variable,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("画面情報の読取りには対応していません。");

    /// <summary>
    /// 実行共有資源を保ち、worker独自の入力状態を作成します。
    /// </summary>
    IWindowsAutomationSession Fork() => throw new NotSupportedException(
        "この自動操作sessionはworkerの分離に対応していません。");

    /// <summary>
    /// 実際のworker Threadへ接続する優先度管理を作成します。
    /// </summary>
    IWorkerThreadPriority? CreateWorkerThreadPriority() => null;

    /// <summary>
    /// 操作エラーをscriptへ公開する場合の本文を返します。既定はhost障害として扱います。
    /// </summary>
    string? GetScriptErrorMessage(string name, Exception exception) => null;

    /// <summary>
    /// objectの操作・列挙エラーをscriptへ公開する本文です。既定はhost障害です。
    /// </summary>
    string? GetObjectScriptErrorMessage(Exception exception) => null;

    /// <summary>
    /// このセッションで識別したCOM操作の失敗かを返します。
    /// </summary>
    /// <remarks>未知のホスト障害・取消・解放失敗は対象に含めません。</remarks>
    bool IsComError(Exception exception) => false;

    /// <summary>
    /// 押下中の入力がなく、一時停止可能かを取得します。
    /// </summary>
    bool CanPause => true;

    /// <summary>
    /// 待機中に入力状態の採取を継続する必要があるかを取得します。
    /// </summary>
    bool RequiresInputPolling => false;

    /// <summary>
    /// 実行中の自動操作が報告する非同期障害を採取する必要があるかを取得します。
    /// </summary>
    bool RequiresAutomationFailurePolling => false;

    /// <summary>
    /// 未通知の非同期障害を1件取り出します。既定では障害を返しません。
    /// </summary>
    WindowsAutomationFailure? TakeAutomationFailure() => null;

    /// <summary>
    /// 入力を送信せず、現在の入力状態を実行内へ採取します。
    /// </summary>
    void PollInput(CancellationToken cancellationToken)
    {
    }

    /// <summary>
    /// 任意の実行制御を接続します。既定実装は使用しません。
    /// </summary>
    void ConfigurePause(ScriptPauseControl? control,
        CancellationToken cancellationToken)
    {
    }

    /// <summary>
    /// 直前のGETITEMで取得した文字列のスナップショットです。
    /// </summary>
    IReadOnlyList<string> AllItems => [];

    /// <summary>
    /// 直前のGETOLEITEMで取得した要素です。
    /// </summary>
    IReadOnlyList<Value> AllOleItems => [];

    /// <summary>
    /// 直前のGETALLWINで取得したウィンドウIDです。
    /// </summary>
    IReadOnlyList<int> AllWindowIds => [];

    /// <summary>
    /// セッション所有objectのmemberを呼び出します。
    /// </summary>
    ValueTask<Value> InvokeMemberAsync(Value target, string name,
        IReadOnlyList<Value> arguments, Value? assigned,
        CancellationToken cancellationToken) => throw new NotSupportedException(
            "objectの操作には対応していません。");

    /// <summary>
    /// VAR/名前付き引数を含むmemberを呼び出し、書戻し用の値を返します。
    /// </summary>
    async ValueTask<WindowsMemberResult> InvokeMemberWithReferencesAsync(
        Value target, string name, IReadOnlyList<WindowsMemberArgument> arguments,
        Value? assigned, CancellationToken cancellationToken)
    {
        if (arguments.Any(argument => argument.ByReference || argument.Name is not null))
        {
            throw new NotSupportedException("COMのVAR/名前付き引数には対応していません。");
        }

        Value[] values = [.. arguments.Select(argument => argument.Value)];
        return new(await InvokeMemberAsync(target, name, values,
            assigned, cancellationToken).ConfigureAwait(false), System.Array.AsReadOnly(values));
    }

    /// <summary>
    /// セッション所有objectのコレクションを列挙します。
    /// </summary>
    ValueTask<IReadOnlyList<Value>> EnumerateObjectsAsync(Value target,
        CancellationToken cancellationToken) => throw new NotSupportedException(
            "objectの列挙には対応していません。");

    /// <summary>
    /// 直前の画像探索結果の変更不可スナップショットです。
    /// </summary>
    WindowsImageResult ImageResult => WindowsImageResult.Empty;

    ValueTask<Value> InvokeAsync(string name,
        IReadOnlyList<Value> arguments,
        CancellationToken cancellationToken);
}

/// <summary>
/// member引数の入力値・VAR指定・名前を保持します。
/// </summary>
public readonly record struct WindowsMemberArgument(Value Value, bool ByReference = false)
{
    /// <summary>
    /// 名前付き引数の名前です。nullは位置引数です。
    /// </summary>
    public string? Name
    {
        get; init;
    }
}

/// <summary>
/// memberの返値と引数順の書戻しsnapshotです。
/// </summary>
public sealed record WindowsMemberResult(Value Value, IReadOnlyList<Value> Arguments);

/// <summary>
/// 画像探索の座標と全件取得配列をまとめます。
/// </summary>
public sealed record WindowsImageResult(int X, int Y,
    IReadOnlyList<int> AllX, IReadOnlyList<int> AllY)
{
    /// <summary>
    /// 未検出時の初期結果です。
    /// </summary>
    public static WindowsImageResult Empty { get; } = new(-999999, -999999, [], []);
}
