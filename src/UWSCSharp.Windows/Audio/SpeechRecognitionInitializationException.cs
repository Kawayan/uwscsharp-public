namespace UWSCSharp.Windows;

/// <summary>
/// 識別済みの認識COM初期化失敗を固定メッセージで表し、原因を保持します。
/// </summary>
internal sealed class SpeechRecognitionInitializationException : InvalidOperationException
{
    internal SpeechRecognitionInitializationException(Exception cause)
        : base("SAPIが存在しないか or 初期化エラー", cause)
    {
        HResult = cause.HResult;
    }
}
