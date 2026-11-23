using System.IO.Pipes;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 戻らない音声APIの代わりに、背景スレッドを保持して親切断を検査します。
/// </summary>
internal static class AudioRunnerProbe
{
    internal static async Task<int> RunAsync(string name)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            return 90;
        }

        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30000);
        return await AudioHostRunner.RunAsync(pipe, new BlockedBackend());
    }

    private sealed class BlockedBackend : IAudioPlaybackBackend
    {
        public void Play(AudioHostPlayRequest request, Action started, CancellationToken token)
        {
            if (!Thread.CurrentThread.IsBackground)
            {
                throw new InvalidOperationException("検証用の保持は背景スレッドで実行してください。");
            }

            started();
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
