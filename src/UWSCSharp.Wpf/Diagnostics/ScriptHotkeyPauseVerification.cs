using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using UWSCSharp.Core;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

internal static class ScriptHotkeyPauseVerification
{
    // OSの一時停止キーと通知を使い、停止済みの状態で配送が保留されることを確認します。
    internal static async Task<string[]> RunAsync(MainWindow window)
    {
        var playback = new MainWindow.PlaybackVerificationAccess(window);
        List<string> checks = [];
        foreach (int key in new[] { 122, 0 })
        {
            Task<int> run = window.RunCommandLineAsync($$"""
                IF !SETHOTKEY({{key}},6,"Pressed") THEN EXITEXIT(99)
                PRINT "pause-hotkey-ready"
                WHILE TRUE
                    SLEEP(0.02)
                WEND
                PROCEDURE Pressed()
                    PRINT "pause-hotkey-delivered"
                    EXITEXIT(7)
                FEND
                """, new ScriptRunOptions([]));
            try
            {
                await WaitAsync(() => playback.Output.Contains("pause-hotkey-ready", StringComparison.Ordinal));
                window.Activate();
                if (Native.GetForegroundWindow() != new WindowInteropHelper(window).Handle)
                {
                    throw new InvalidOperationException("検証画面を入力先にできません。");
                }

                await SendPauseProbeAsync([new(164, 0), new(120, 0),
                    new(120, 2), new(164, 2)]);
                await WaitAsync(() => playback.IsPaused);
                List<PauseProbeInput> inputs = [new(162, 0), new(160, 0)];
                if (key != 0)
                {
                    inputs.Add(new((ushort)key, 0));
                    inputs.Add(new((ushort)key, 2));
                }

                inputs.Add(new(160, 2));
                inputs.Add(new(162, 2));
                await SendPauseProbeAsync([.. inputs]);
                await Task.Delay(300);
                if (run.IsCompleted || !playback.IsPaused
                    || playback.Output.Contains("pause-hotkey-delivered", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("一時停止中に通知が配送されました。");
                }

                await SendPauseProbeAsync([new(164, 0), new(120, 0),
                    new(120, 2), new(164, 2)]);
                int exit = await run.WaitAsync(TimeSpan.FromSeconds(10));
                if (exit != 7 || !playback.Output.Contains("pause-hotkey-delivered", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"再開後の通知を確認できません: {exit}");
                }

                checks.Add($"paused-hotkey-{key}-delivered-after-resume");
            }
            finally
            {
                // 途中失敗でも注入したキーを解放し、実行の取消と回収を行います。
                try
                {
                    await SendPauseProbeAsync([new(120, 2), new(122, 2),
                        new(160, 2), new(162, 2), new(164, 2)]);
                }
                finally
                {
                    playback.RequestStop();
                    await run.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            async Task WaitAsync(Func<bool> ready)
            {
                var elapsed = Stopwatch.StartNew();
                while (!ready())
                {
                    if (run.IsCompleted || elapsed.Elapsed > TimeSpan.FromSeconds(10))
                    {
                        throw new InvalidOperationException("一時停止検証の準備が完了しません。");
                    }

                    await Task.Delay(10);
                }
            }
        }

        return [.. checks];
    }

    private static Task SendPauseProbeAsync(PauseProbeInput[] inputs) => Task.Run(() =>
    {
        if (IntPtr.Size != 8 || PauseProbeSendInput((uint)inputs.Length, inputs, 40) != inputs.Length)
        {
            throw new InvalidOperationException("検証用キーを送信できません。");
        }
    });

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct PauseProbeInput
    {
        [FieldOffset(0)] private uint type;
        [FieldOffset(8)] private ushort key;
        [FieldOffset(12)] private uint flags;

        internal PauseProbeInput(ushort key, uint flags)
        {
            this = default;
            type = 1;
            this.key = key;
            this.flags = flags;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendInput")]
    private static extern uint PauseProbeSendInput(uint count, PauseProbeInput[] inputs, int size);
}
