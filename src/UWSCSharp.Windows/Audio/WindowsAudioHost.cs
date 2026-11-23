using System.IO.Pipes;
using System.Runtime.Versioning;

namespace UWSCSharp.Windows;

/// <summary>
/// 通常のCLI・WPF起動より先に、専用の音声要求を処理します。
/// </summary>
public static class WindowsAudioHost
{
    private static readonly TimeSpan PipeConnectionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 音声子プロセスの起動を処理した場合にtrueを返します。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool TryRun(string[] arguments, out int exitCode)
    {
        exitCode = 2;
        if (arguments.Length == 0 || arguments[0] != AudioHostProcess.HostArgument)
        {
            return false;
        }

        if (arguments is not [_, string name] || !ValidPipeName(name))
        {
            return true;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", name,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using (var timeout = new CancellationTokenSource(PipeConnectionTimeout))
            {
                pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            }

            exitCode = AudioHostRunner.RunAsync(pipe,
                new NativeAudioPlaybackBackend(new AudioNativePlatform())).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 音声専用の起動失敗を、通常の画面や標準出力へ流しません。
            exitCode = 2;
        }

        return true;
    }

    internal static bool ValidPipeName(string name)
    {
        const string prefix = "UWSCSharp-audio-";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && name.Length == prefix.Length + 32
            && Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _);
    }
}
