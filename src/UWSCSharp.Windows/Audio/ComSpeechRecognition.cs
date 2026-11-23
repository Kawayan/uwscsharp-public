using System.Runtime.Versioning;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 専用STAで認識context、既定入力、文法と通知接続を所有します。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ComSpeechRecognition : ISpeechRecognitionSession
{
    private const string InputCategory =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\AudioInput";
    private readonly ComSession session;
    private readonly Value grammar;
    private readonly IComEventConnection connection;
    private readonly SpeechRecognitionControlQueue operations = new();
    private readonly object gate = new();
    private Task? disposal;
    private bool closing;
    private bool startAttempted;

    private ComSpeechRecognition(ComSession session, Value grammar,
        IComEventConnection connection)
    {
        this.session = session;
        this.grammar = grammar;
        this.connection = connection;
    }

    internal static async Task<ComSpeechRecognition> CreateAsync(
        Action<string> receive, IComRuntime? runtime = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(receive);
        token.ThrowIfCancellationRequested();
        var session = new ComSession(runtime);
        try
        {
            Value context = RequireObject(await session.CreateAsync(
                "SAPI.SpInProcRecoContext", false, token).ConfigureAwait(false));
            Value category = RequireObject(await session.CreateAsync(
                "SAPI.SpObjectTokenCategory", false, token).ConfigureAwait(false));
            await session.InvokeAsync(category, "SetId",
                [Value.String(InputCategory), False()], null, token)
                .ConfigureAwait(false);
            Value input = RequireObject(await session.CreateAsync(
                "SAPI.SpObjectToken", false, token).ConfigureAwait(false));
            Value defaultId = await session.InvokeAsync(category, "Default",
                [], null, token).ConfigureAwait(false);
            if (defaultId.Kind != ValueKind.String)
            {
                throw new SpeechRecognitionInitializationException(
                    new InvalidDataException(
                        "既定の音声入力IDが文字列ではありません。"));
            }

            await session.InvokeAsync(input, "SetId",
                [defaultId, Value.String(InputCategory), False()], null, token)
                .ConfigureAwait(false);
            Value recognizer = RequireObject(await session.InvokeAsync(context,
                "Recognizer", [], null, token).ConfigureAwait(false));
            await session.InvokeAsync(recognizer, "AudioInput", [], input, token)
                .ConfigureAwait(false);
            Value grammar = RequireObject(await session.InvokeAsync(context,
                "CreateGrammar", [Integer(0)], null, token).ConfigureAwait(false));
            IComEventConnection connection = await session.ConnectSpeechRecognitionAsync(
                context, receive, token).ConfigureAwait(false);
            return new(session, grammar, connection);
        }
        catch (Exception primary)
        {
            SpeechRecognitionInitializationException? initializationFailure =
                session.GetScriptErrorMessage(primary) is not null
                    ? new(primary) : null;
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(initializationFailure ?? primary, cleanup);
            }

            if (initializationFailure is not null)
            {
                throw initializationFailure;
            }

            throw;
        }
    }

    /// <summary>
    /// 既存の文法を停止して自由文認識を開始します。
    /// </summary>
    public Task StartDictationAsync(CancellationToken token = default,
        Action? beforeStart = null) =>
        RunAsync(async () =>
        {
            await PrepareStartAsync(token).ConfigureAwait(false);
            beforeStart?.Invoke();
            await InvokeAsync("DictationSetState", [Integer(1)], token)
                .ConfigureAwait(false);
        }, token);

    /// <summary>
    /// 呼出し側が用意した文法を動的に読み込み、ルール0を開始します。
    /// </summary>
    public Task StartGrammarFileAsync(string path,
        CancellationToken token = default, Action? beforeStart = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return RunAsync(async () =>
        {
            await PrepareStartAsync(token).ConfigureAwait(false);
            beforeStart?.Invoke();
            await LoadAndActivateAsync(path, token).ConfigureAwait(false);
        }, token);
    }

    /// <summary>
    /// 旧文法を停止してから登録語を保存し、読込みと開始後に回収します。
    /// </summary>
    public Task StartWordsAsync(IEnumerable<string> words,
        CancellationToken token = default, Action? beforeStart = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        return StartEvaluatedWordsAsync(_ => Task.FromResult(words), token, beforeStart);
    }

    /// <summary>
    /// 停止と結果消去の後に、呼出し側の文脈で語彙を非同期評価します。
    /// </summary>
    public Task StartEvaluatedWordsAsync(
        Func<CancellationToken, Task<IEnumerable<string>>> evaluate,
        CancellationToken token = default, Action? beforeStart = null)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        return RunAsync(async () =>
        {
            await PrepareStartAsync(token).ConfigureAwait(WorkerThreadContext.IsCurrent);
            beforeStart?.Invoke();
            token.ThrowIfCancellationRequested();
            IEnumerable<string> words = await evaluate(token)
                .ConfigureAwait(WorkerThreadContext.IsCurrent);
            token.ThrowIfCancellationRequested();
            await SpeechRecognitionGrammarFile.UseAsync(words,
                LoadAndActivateAsync, token).ConfigureAwait(false);
        }, token);
    }

    private async Task LoadAndActivateAsync(string path, CancellationToken token)
    {
        await InvokeAsync("CmdLoadFromFile", [Value.String(path), Integer(1)], token)
            .ConfigureAwait(false);
        await InvokeAsync("CmdSetRuleIdState", [Integer(0), Integer(1)], token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 保持している登録語認識と自由文認識を停止します。
    /// </summary>
    public Task StopAsync(CancellationToken token = default) =>
        RunAsync(() => StopBeforeControlAsync(token), token);

    /// <summary>
    /// 通知中の失敗を取り出します。呼出し側で待機解除と報告に使います。
    /// </summary>
    public Exception? TakeCallbackFailure() => connection.TakeCallbackFailure();

    public string? GetScriptErrorMessage(Exception failure) => session.GetScriptErrorMessage(failure);

    private Task RunAsync(Func<Task> operation, CancellationToken token) =>
        operations.RunAsync(async () =>
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
            }

            await operation().ConfigureAwait(WorkerThreadContext.IsCurrent);
        }, token);

    private async Task PrepareStartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool replacing = startAttempted;
        startAttempted = true;
        if (replacing)
        {
            await StopBeforeControlAsync(token).ConfigureAwait(false);
        }
    }

    private async Task StopBeforeControlAsync(CancellationToken token)
    {
        try
        {
            await StopCoreAsync(token).ConfigureAwait(false);
        }
        catch (Exception failure) when (session.GetScriptErrorMessage(failure) is not null)
        {
            // 原版の停止用例外処理へ合わせ、識別済みのCOM停止失敗だけを消費します。
            // 取消と未知の障害、終了時の停止・回収失敗はこの処理へ流しません。
            token.ThrowIfCancellationRequested();
        }
    }

    private async Task StopCoreAsync(CancellationToken token)
    {
        await InvokeAsync("CmdSetRuleIdState", [Integer(0), Integer(0)], token)
            .ConfigureAwait(false);
        await InvokeAsync("DictationSetState", [Integer(0)], token)
            .ConfigureAwait(false);
    }

    private async Task InvokeAsync(string name, Value[] arguments,
        CancellationToken token) =>
        await session.InvokeAsync(grammar, name, arguments, null, token)
            .ConfigureAwait(false);

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closing = true;
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            List<Exception> errors = [];
            // 一方の停止が失敗しても、他方の停止と通知解除・参照回収を試みます。
            foreach (string name in new[] { "CmdSetRuleIdState", "DictationSetState" })
            {
                try
                {
                    Value[] arguments = name == "CmdSetRuleIdState"
                        ? [Integer(0), Integer(0)] : [Integer(0)];
                    await InvokeAsync(name, arguments, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    errors.Add(failure);
                }
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                errors.Add(failure);
            }

            if (errors.Count != 0)
            {
                throw new AggregateException(errors);
            }
        }
        finally
        {
            operations.Release();
        }
    }

    private static Value RequireObject(Value value) =>
        value.Kind == ValueKind.Object ? value
            : throw new SpeechRecognitionInitializationException(
                new InvalidDataException(
                    "音声認識の作成結果がオブジェクトではありません。"));

    private static Value Integer(int value) => Value.Numeric(value) with
    {
        NativeVariantType = 3,
        NativeScalar = value
    };

    private static Value False() => Value.Numeric(0) with
    {
        NativeVariantType = 11,
        NativeScalar = false
    };
}
