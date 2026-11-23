namespace UWSCSharp.Windows;

/// <summary>
/// 認識文法の制御、通知失敗と所有終了を提供します。
/// </summary>
internal interface ISpeechRecognitionSession : IAsyncDisposable
{
    Task StartDictationAsync(CancellationToken token = default,
        Action? beforeStart = null);

    Task StartGrammarFileAsync(string path, CancellationToken token = default,
        Action? beforeStart = null);

    Task StartWordsAsync(IEnumerable<string> words, CancellationToken token = default,
        Action? beforeStart = null);

    Task StartEvaluatedWordsAsync(
        Func<CancellationToken, Task<IEnumerable<string>>> evaluate,
        CancellationToken token = default, Action? beforeStart = null);

    Task StopAsync(CancellationToken token = default);

    Exception? TakeCallbackFailure();

    string? GetScriptErrorMessage(Exception failure) => null;
}
