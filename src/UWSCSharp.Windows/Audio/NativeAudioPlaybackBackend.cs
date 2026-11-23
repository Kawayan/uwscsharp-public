using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace UWSCSharp.Windows;

/// <summary>
/// 呼出しが戻らないAPIも、専用子プロセスの寿命内へ閉じ込めます。
/// </summary>
internal sealed class NativeAudioPlaybackBackend(IAudioNativePlatform platform) : IAudioPlaybackBackend
{
    private const string Alias = "uwsc_audio_playback";

    public void Play(AudioHostPlayRequest request, Action started, CancellationToken token)
    {
        AudioHostProtocol.Validate(new(AudioHostMessageType.Play, request));
        token.ThrowIfCancellationRequested();
        if (request.Kind == AudioPlaybackKind.Beep)
        {
            started();
            for (int index = 0; index < 2; index++)
            {
                token.ThrowIfCancellationRequested();
                platform.Beep(800, 200);
            }

            token.ThrowIfCancellationRequested();
            return;
        }

        if (request.Kind == AudioPlaybackKind.SystemEvent)
        {
            if (request.Source.Length > 255 || request.Source.Contains('\\') || request.Source.Contains('/'))
            {
                throw new ArgumentException("イベント名の長さまたは文字が不正です。");
            }

            string file = platform.EventFile(request.Source);
            Play(new(AudioPlaybackKind.WaveFile, file), started, token);
            return;
        }

        if (!Path.IsPathFullyQualified(request.Source) || request.Source.Contains('"'))
        {
            throw new ArgumentException("音声ファイルには引用符を含まない絶対パスを指定してください。");
        }

        string type = request.Kind switch
        {
            AudioPlaybackKind.WaveFile => "waveaudio",
            AudioPlaybackKind.MidiFile => "sequencer",
            AudioPlaybackKind.VideoFile => "avivideo",
            _ => throw new ArgumentException("音声の種類が不正です。")
        };
        nint video = 0;
        bool opened = false;
        Exception? primary = null;
        try
        {
            string window = "";
            if (request.Kind == AudioPlaybackKind.VideoFile)
            {
                video = platform.CreateVideoWindow();
                window = $" parent {((nuint)video).ToString(CultureInfo.InvariantCulture)} style child";
            }

            platform.Mci($"open \"{request.Source}\" type {type} alias {Alias}{window}");
            opened = true;
            if (request.OutputDevice is { } device)
            {
                if (device >= platform.WaveOutputCount)
                {
                    throw new AudioPlaybackException("WAV出力デバイスの番号が範囲外です。");
                }

                platform.Mci($"set {Alias} output {device.ToString(CultureInfo.InvariantCulture)}");
            }

            if (request.Kind == AudioPlaybackKind.VideoFile)
            {
                platform.Mci($"window {Alias} state hide");
            }

            token.ThrowIfCancellationRequested();
            platform.Mci($"play {Alias}");
            started();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string mode = platform.Mci($"status {Alias} mode").Trim();
                if (mode.Equals("stopped", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (!mode.Equals("playing", StringComparison.OrdinalIgnoreCase)
                    && !mode.Equals("seeking", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AudioPlaybackException($"予期しない再生状態です: {mode}");
                }

                platform.WaitForTick(token);
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            List<Exception> cleanup = [];
            try
            {
                if (opened)
                {
                    platform.Mci($"close {Alias}");
                }
            }
            catch (Exception error)
            {
                cleanup.Add(error);
            }

            try
            {
                if (video != 0)
                {
                    platform.DestroyVideoWindow(video);
                }
            }
            catch (Exception error)
            {
                cleanup.Add(error);
            }

            if (cleanup.Count != 0)
            {
                Exception error = cleanup.Count == 1 ? cleanup[0]
                    : new AggregateException("音声の解放に失敗しました。", cleanup);
                if (primary is not null)
                {
                    primary.Data["音声の解放"] = error;
                }
                else
                {
                    ExceptionDispatchInfo.Capture(error).Throw();
                }
            }
        }
    }
}

/// <summary>
/// 再生手順を音声APIなしでも検査するための接点です。
/// </summary>
internal interface IAudioNativePlatform
{
    uint WaveOutputCount
    {
        get;
    }

    string Mci(string command);

    void Beep(uint frequency, uint milliseconds);

    string EventFile(string name);

    nint CreateVideoWindow();

    void DestroyVideoWindow(nint window);

    void WaitForTick(CancellationToken token);
}

/// <summary>
/// WinMMとBeepを、専用子プロセスから呼びます。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class AudioNativePlatform : IAudioNativePlatform
{
    public uint WaveOutputCount => waveOutGetNumDevs();

    public string Mci(string command)
    {
        var text = new StringBuilder(1024);
        uint code = mciSendStringW(command, text, (uint)text.Capacity, 0);
        if (code != 0)
        {
            var description = new StringBuilder(512);
            _ = mciGetErrorStringW(code, description, (uint)description.Capacity);
            throw new AudioPlaybackException($"MCI: {description}（{code}）", code);
        }

        return text.ToString();
    }

    public void Beep(uint frequency, uint milliseconds)
    {
        if (!NativeBeep(frequency, milliseconds))
        {
            uint code = unchecked((uint)Marshal.GetLastWin32Error());
            throw new AudioPlaybackException($"BEEPの再生に失敗しました（{code}）。", code);
        }
    }

    public string EventFile(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"AppEvents\Schemes\Apps\.Default\" + name + @"\.Current");
        if (key?.GetValue("", null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) is not string file
            || string.IsNullOrWhiteSpace(file))
        {
            throw new AudioPlaybackException("イベントに音声ファイルが登録されていません。");
        }

        return Environment.ExpandEnvironmentVariables(file);
    }

    public nint CreateVideoWindow()
    {
        nint window = Native.CreateWindowExW(0, ControlClassNames.Uppercase.Static, "UWSCSharp audio owner",
            0x80000000, 0, 0, 1, 1, 0, 0, 0, 0);
        if (window == 0)
        {
            throw new AudioPlaybackException("映像の非表示親画面を作成できません。",
                unchecked((uint)Marshal.GetLastWin32Error()));
        }

        return window;
    }

    public void DestroyVideoWindow(nint window)
    {
        if (!Native.DestroyWindow(window))
        {
            throw new AudioPlaybackException("映像の非表示親画面を解放できません。",
                unchecked((uint)Marshal.GetLastWin32Error()));
        }
    }

    public void WaitForTick(CancellationToken token)
    {
        // 映像ドライバーのメッセージも、作成した同じスレッドで処理します。
        for (int index = 0; index < 100 && Native.PeekMessageW(out Native.Message message, 0, 0, 0, 1); index++)
        {
            token.ThrowIfCancellationRequested();
            _ = Native.TranslateMessage(ref message);
            _ = Native.DispatchMessageW(ref message);
        }

        if (token.WaitHandle.WaitOne(20))
        {
            token.ThrowIfCancellationRequested();
        }
    }
}
