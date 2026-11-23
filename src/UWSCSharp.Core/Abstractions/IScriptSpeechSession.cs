namespace UWSCSharp.Core;

/// <summary>
/// スクリプトの発声要求を実行先に渡します。
/// </summary>
internal interface IScriptSpeechSession
{
    Task SubmitSpeechAsync(bool onlySapi, SpeakRequest request,
        CancellationToken token);
}
