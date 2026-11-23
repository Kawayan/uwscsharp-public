namespace UWSCSharp.Windows;

/// <summary>
/// 専用子プロセスの1つのスレッドで再生し、開始と終了を分けます。
/// </summary>
internal interface IAudioPlaybackBackend
{
    void Play(AudioHostPlayRequest request, Action started, CancellationToken token);
}

/// <summary>
/// 音声APIのエラー番号を通知へ引き継ぎます。
/// </summary>
internal sealed class AudioPlaybackException(string message, uint? nativeCode = null)
    : IOException(message)
{
    internal uint? NativeCode { get; } = nativeCode;
}
