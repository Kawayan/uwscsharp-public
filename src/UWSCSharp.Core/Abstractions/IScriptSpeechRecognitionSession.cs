namespace UWSCSharp.Core;

/// <summary>
/// 実行共有の認識制御と、停止後の語彙評価を接続します。
/// </summary>
internal interface IScriptSpeechRecognitionSession
{
    ValueTask<bool> ReadRecognitionFlagAsync(Value value, string function,
        CancellationToken token);

    ValueTask<string> FormatRecognitionWordAsync(Value value,
        CancellationToken token);

    Task StartRecognitionAsync(
        Func<CancellationToken, Task<IEnumerable<string>>>? evaluate = null,
        CancellationToken token = default);

    Task StopRecognitionAsync(CancellationToken token = default);

    Task<string> ReadRecognitionAsync(bool wait,
        CancellationToken token = default);
}
