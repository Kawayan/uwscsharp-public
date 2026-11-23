using System.Text.Json.Serialization;

namespace UWSCSharp.Windows;

/// <summary>
/// 音声子プロセスへ渡す再生処理の種類です。
/// </summary>
internal enum AudioPlaybackKind
{
    WaveFile,
    MidiFile,
    VideoFile,
    SystemEvent,
    Beep
}

/// <summary>
/// 通信上の再生要求です。スクリプト引数の解釈とは分離します。
/// </summary>
internal sealed record AudioHostPlayRequest(
    [property: JsonRequired] AudioPlaybackKind Kind,
    [property: JsonRequired] string Source, uint? OutputDevice = null);

/// <summary>
/// 音声処理の失敗理由と、取得できた場合のOSエラー番号です。
/// </summary>
internal sealed record AudioHostFailure(string Message, uint? NativeCode = null);

/// <summary>
/// 1回の再生に使う要求と通知の種類です。
/// </summary>
internal enum AudioHostMessageType
{
    Play,
    Started,
    Completed,
    Failed
}

/// <summary>
/// 版を明示した音声子プロセスとの通信単位です。
/// </summary>
internal sealed record AudioHostMessage(
    [property: JsonRequired] AudioHostMessageType Type,
    AudioHostPlayRequest? Request = null, AudioHostFailure? Failure = null,
    [property: JsonRequired] int Version = AudioHostProtocol.Version);
