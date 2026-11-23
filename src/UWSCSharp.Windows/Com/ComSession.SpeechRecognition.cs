using System.Runtime.InteropServices.ComTypes;
using UWSCSharp.Core;

namespace UWSCSharp.Windows;

internal sealed partial class ComSession
{
    /// <summary>
    /// 認識結果の参照を通知中に解放し、文字列だけを配送します。
    /// </summary>
    internal ValueTask<IComEventConnection> ConnectSpeechRecognitionAsync(
        Value context, Action<string> receive, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(receive);
        return ConnectEventAsync(context, "_ISpeechRecoContextEvents", "Recognition",
            (ref DISPPARAMS parameters) =>
            {
                if (Environment.CurrentManagedThreadId != staThreadId)
                {
                    throw new InvalidOperationException("音声認識の通知は所有STAで処理してください。");
                }

                var frame = new NativeComEventArguments(in parameters);
                if (frame.Count != 4)
                {
                    throw new InvalidDataException("音声認識の通知引数は4個必要です。");
                }

                // 認識中の通知には、登録時だけ使う取消tokenを持ち越しません。
                string text = ComSpeechRecognitionText.Read(runtime, frame.Read(3));
                receive(text);
            }, token);
    }
}
