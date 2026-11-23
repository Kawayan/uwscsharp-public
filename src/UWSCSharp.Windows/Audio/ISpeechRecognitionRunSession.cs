using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 公開接続前の認識制御と結果取得を実行単位で共有する内部接点です。
/// </summary>
internal interface ISpeechRecognitionRunSession
{
    Task StartRecognitionAsync(
        Func<CancellationToken, Task<IEnumerable<string>>>? evaluate = null,
        CancellationToken token = default);

    Task StopRecognitionAsync(CancellationToken token = default);

    Task StartRecognitionWordsAsync(IReadOnlyList<Expr> expressions,
        IEvaluationContext context, bool preserveSpecialCharacters,
        CancellationToken token = default);

    Task<string> ReadRecognitionAsync(bool wait,
        CancellationToken token = default);
}
