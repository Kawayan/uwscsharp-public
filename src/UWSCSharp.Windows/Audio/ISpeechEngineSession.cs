using UWSCSharp.Core;

namespace UWSCSharp.Windows;

/// <summary>
/// 公開関数への接続前に、実行単位の発声所有を扱う内部接点です。
/// </summary>
internal interface ISpeechEngineSession
{
    Task SendSpeechAsync(SpeechEngine engine, SpeechDispatchRequest request,
        bool discardPending = false, CancellationToken token = default);

    /// <summary>
    /// 平行処理では送信結果、同期処理では発声完了を返します。
    /// </summary>
    Task SubmitSpeechAsync(SpeechEngine engine, SpeakRequest request,
        CancellationToken token = default);

    /// <summary>
    /// 平行処理ではキューへの登録後に戻り、送信失敗は別途通知します。
    /// </summary>
    Task SubmitSpeechAsync(bool onlySapi, SpeakRequest request,
        CancellationToken token = default);

    Task WaitForSpeechAsync(CancellationToken token = default);
}
