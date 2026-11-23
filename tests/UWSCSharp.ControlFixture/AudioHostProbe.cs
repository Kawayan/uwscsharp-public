using System.Diagnostics;
using System.IO.Pipes;
using UWSCSharp.Windows;

namespace UWSCSharp.ControlFixture;

/// <summary>
/// 音声APIを呼ばず、通知と待機・子孫の所有だけを検査する子プロセスです。
/// </summary>
internal static class AudioHostProbe
{
    internal static async Task<int> RunAsync(string name)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            return 90;
        }

        using var pipe = new NamedPipeClientStream(".", name,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(30000);
        AudioHostMessage request = await AudioHostProtocol.ReadAsync(pipe)
            ?? throw new EndOfStreamException();
        string receipt = request.Request!.Source;
        string mode = Path.GetFileName(receipt);
        await MarkAsync(receipt, Environment.ProcessId);
        if (mode == "hold-start")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        if (mode == "early-eof")
        {
            return 0;
        }

        if (mode == "failed-start")
        {
            await AudioHostProtocol.WriteAsync(pipe,
                new(AudioHostMessageType.Failed, Failure: new("起動失敗", 5)));
            return 1;
        }

        if (mode == "truncated")
        {
            await pipe.WriteAsync(new byte[] { 100, 0, 0, 0, 123 });
            return 0;
        }

        await AudioHostProtocol.WriteAsync(pipe, new(AudioHostMessageType.Started));
        if (mode == "child")
        {
            using Process child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!,
                "--audio-host-child")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            })!;
            await MarkAsync(receipt + ".child", child.Id);
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        if (mode == "hold-complete")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        if (mode == "duplicate")
        {
            await AudioHostProtocol.WriteAsync(pipe, new(AudioHostMessageType.Started));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        if (mode == "failed-play")
        {
            await AudioHostProtocol.WriteAsync(pipe,
                new(AudioHostMessageType.Failed, Failure: new("再生失敗", 9)));
            return 1;
        }

        await AudioHostProtocol.WriteAsync(pipe, new(AudioHostMessageType.Completed));
        if (mode == "completed-eof")
        {
            return 0;
        }

        if (mode == "bad-exit")
        {
            return 7;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task MarkAsync(string path, int id)
    {
        await File.WriteAllTextAsync(path + ".writing", id.ToString());
        File.Move(path + ".writing", path);
    }
}
