namespace UWSCSharp.Windows;

public sealed partial class WindowsAutomation
{
    private sealed partial class Session : IAudioPlaybackSession
    {
        public AudioPlaybackTicket PlayAudio(AudioHostPlayRequest request, CancellationToken token)
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                token.ThrowIfCancellationRequested();
                return resources.PlayAudio(request);
            }
        }

        public Task StopAudioAsync()
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return resources.StopAudioAsync();
            }
        }

        public Task ReleaseAudioAsync(AudioPlaybackTicket ticket)
        {
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return resources.ReleaseAudioAsync(ticket);
            }
        }
    }

    private sealed partial class RunResources
    {
        private readonly CancellationTokenSource audioStop = new();
        private readonly HashSet<CancellationToken> audioRootTokens = [];
        private readonly List<CancellationTokenRegistration> audioRegistrations = [];
        private AudioPlaybackOwner? audio;
        private Task? audioStopping;

        internal void ConfigureAudioCancellation(CancellationToken token)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                if (token.CanBeCanceled && audioRootTokens.Add(token))
                {
                    audioRegistrations.Add(token.UnsafeRegister(
                        static state => ((RunResources)state!).RequestAudioStop(), this));
                }
            }
        }

        internal AudioPlaybackTicket PlayAudio(AudioHostPlayRequest request)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closing, this);
                foreach (CancellationToken token in audioRootTokens)
                {
                    token.ThrowIfCancellationRequested();
                }

                audioStop.Token.ThrowIfCancellationRequested();
                audio ??= audioPlaybackFactory is not null
                    ? new AudioPlaybackOwner(audioPlaybackFactory, audioStop.Token)
                    : new AudioPlaybackOwner(audioHost ?? throw new NotSupportedException(
                        "音声再生には、対応した実行元の起動情報を指定してください。"), audioStop.Token);
                return audio.Replace(request);
            }
        }

        internal Task StopAudioAsync()
        {
            lock (gate)
            {
                return audio?.StopAsync() ?? Task.CompletedTask;
            }
        }

        internal Task ReleaseAudioAsync(AudioPlaybackTicket ticket)
        {
            ArgumentNullException.ThrowIfNull(ticket);
            lock (gate)
            {
                return audio?.ReleaseAsync(ticket)
                    ?? throw new ArgumentException("この実行に属する再生要求ではありません。", nameof(ticket));
            }
        }

        private void RequestAudioStop()
        {
            lock (gate)
            {
                audioStopping ??= audioStop.CancelAsync();
            }
        }

        private async Task DisposeAudioAsync(List<Exception> errors)
        {
            AudioPlaybackOwner? owned;
            Task stopping;
            CancellationTokenRegistration[] registrations;
            lock (gate)
            {
                owned = audio;
                stopping = audioStopping ??= audioStop.CancelAsync();
                registrations = [.. audioRegistrations];
            }

            try
            {
                await stopping.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                if (owned is not null)
                {
                    // workerの終了待ちより先に、同期再生の待機も解除します。
                    await owned.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            foreach (CancellationTokenRegistration registration in registrations)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }

            audioStop.Dispose();
        }
    }
}
