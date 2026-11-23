using System.Runtime.Versioning;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

[SupportedOSPlatform("windows")]
internal sealed class BasicRecordingDevice(BasicRecorder recorder) : IRecordingDevice
{
    /// <summary>
    /// 記録スレッドが動作中かを取得します。
    /// </summary>
    public bool IsRunning => recorder.IsRunning;

    /// <summary>
    /// 開始時の保持入力の解放待ちかを取得します。
    /// </summary>
    public bool WaitingForInitialRelease => recorder.WaitingForInitialRelease;

    /// <summary>
    /// 収集上限で欠落したかを取得します。
    /// </summary>
    public bool Overflowed => recorder.Overflowed;

    /// <summary>
    /// フック復旧による欠落の警告を取得します。
    /// </summary>
    public string Warning => recorder.RecordingWarning;

    /// <summary>
    /// フックの登録完了を待ちます。
    /// </summary>
    public Task StartAsync() => recorder.StartAsync();

    /// <summary>
    /// 停止と記録結果の確定を行います。
    /// </summary>
    public string Stop() => recorder.Stop();

    /// <summary>
    /// 記録スレッドとフックを解放します。
    /// </summary>
    public void Dispose() => recorder.Dispose();
}
