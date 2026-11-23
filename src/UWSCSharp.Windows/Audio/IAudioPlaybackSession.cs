namespace UWSCSharp.Windows;

/// <summary>
/// 実行全体で共有する再生へ、各セッションから要求を渡します。
/// </summary>
internal interface IAudioPlaybackSession
{
    AudioPlaybackTicket PlayAudio(AudioHostPlayRequest request, CancellationToken token = default);

    Task StopAudioAsync();

    Task ReleaseAudioAsync(AudioPlaybackTicket ticket);
}
