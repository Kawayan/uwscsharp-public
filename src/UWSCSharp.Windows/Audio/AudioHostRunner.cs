using System.Text;

namespace UWSCSharp.Windows;

/// <summary>
/// 子側の要求を1回だけ処理し、親の切断では終了待ちを続けません。
/// </summary>
internal static class AudioHostRunner
{
    private static readonly TimeSpan StartupRequestTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<int> RunAsync(Stream stream, IAudioPlaybackBackend backend,
        CancellationToken token = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? playback = null;
        Task<AudioHostMessage?>? parent = null;
        int started = 0;
        try
        {
            AudioHostMessage? message;
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                startup.CancelAfter(StartupRequestTimeout);
                message = await AudioHostProtocol.ReadAsync(stream, startup.Token).ConfigureAwait(false);
            }

            if (message is not { Type: AudioHostMessageType.Play, Request: { } request })
            {
                return 2;
            }

            parent = AudioHostProtocol.ReadAsync(stream, lifetime.Token);
            playback = Task.Factory.StartNew(() => backend.Play(request, () =>
            {
                if (Interlocked.Exchange(ref started, 1) != 0)
                {
                    throw new InvalidDataException("開始通知が重複しています。");
                }

                AudioHostProtocol.WriteAsync(stream, new(AudioHostMessageType.Started), lifetime.Token)
                    .GetAwaiter().GetResult();
            }, lifetime.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            await Task.WhenAny(playback, parent).WaitAsync(token).ConfigureAwait(false);
            if (parent.IsCompleted)
            {
                // 切断や追加要求では、戻らない音声APIを待たずに子を終了します。
                _ = parent.Exception;
                return 2;
            }

            AudioHostMessage terminal;
            int exit;
            try
            {
                await playback.ConfigureAwait(false);
                if (Volatile.Read(ref started) == 0)
                {
                    throw new InvalidDataException("開始通知なしで再生が終了しました。");
                }

                terminal = new(AudioHostMessageType.Completed);
                exit = 0;
            }
            catch (Exception error)
            {
                terminal = new(AudioHostMessageType.Failed, Failure: Failure(error));
                exit = 1;
            }

            await AudioHostProtocol.WriteAsync(stream, terminal, token).ConfigureAwait(false);
            return exit;
        }
        catch (Exception)
        {
            // 不正な要求や切断で、通常UIやエラーダイアログを開きません。
            return 2;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            if (parent is not null)
            {
                try
                {
                    await parent.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 本体の結果は既に決まり、読取りの取消だけを回収します。
                }
            }

            if (playback is not null)
            {
                _ = playback.ContinueWith(task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted
                        | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private static AudioHostFailure Failure(Exception error)
    {
        string text = error.Message;
        if (error.Data["音声の解放"] is Exception cleanup)
        {
            text += " / 音声の解放: " + cleanup.Message;
        }

        var message = new StringBuilder();
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (message.Length + rune.Utf16SequenceLength > 4096)
            {
                break;
            }

            message.Append(rune);
        }

        if (message.Length == 0)
        {
            message.Append("音声処理に失敗しました。");
        }

        return new(message.ToString(), (error as AudioPlaybackException)?.NativeCode);
    }
}
