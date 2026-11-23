using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 偽のAPIだけで再生手順を確認し、音声・画面・実プロセスを使用しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeAudioPlaybackBackendTests
{
    private const string FilePath = @"C:\音声😀\test media.wav";

    [Theory]
    [InlineData((int)AudioPlaybackKind.WaveFile, "waveaudio")]
    [InlineData((int)AudioPlaybackKind.MidiFile, "sequencer")]
    [InlineData((int)AudioPlaybackKind.VideoFile, "avivideo")]
    public void FileKindsPlayAndCloseOnTheCallingThread(int rawKind, string type)
    {
        var kind = (AudioPlaybackKind)rawKind;
        var platform = new Platform();
        int thread = Environment.CurrentManagedThreadId;
        int started = 0;
        new NativeAudioPlaybackBackend(platform).Play(new(kind, FilePath), () =>
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            Assert.Contains("play uwsc_audio_playback", platform.Commands);
            started++;
        }, default);
        Assert.StartsWith($"open \"{FilePath}\" type {type} alias uwsc_audio_playback", platform.Commands[0]);
        Assert.Equal("close uwsc_audio_playback", platform.Commands[^1]);
        Assert.Equal(1, started);
        Assert.All(platform.Threads, value => Assert.Equal(thread, value));
        if (kind == AudioPlaybackKind.VideoFile)
        {
            Assert.EndsWith(" parent 123 style child", platform.Commands[0]);
            Assert.Equal(1, platform.DestroyedVideoWindows);
            Assert.True(platform.Commands.IndexOf("window uwsc_audio_playback state hide")
                < platform.Commands.IndexOf("play uwsc_audio_playback"));
        }
    }

    [Fact]
    public void ExplicitOutputIsCheckedAndSetBeforePlay()
    {
        var platform = new Platform();
        new NativeAudioPlaybackBackend(platform).Play(new(AudioPlaybackKind.WaveFile, FilePath, 1), () => { }, default);
        Assert.Contains("set uwsc_audio_playback output 1", platform.Commands);
        Assert.True(platform.Commands.IndexOf("set uwsc_audio_playback output 1")
            < platform.Commands.IndexOf("play uwsc_audio_playback"));
        Assert.Equal(1, platform.OutputQueries);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void InvalidOutputClosesWithoutStarting(uint device)
    {
        var platform = new Platform();
        Assert.Throws<AudioPlaybackException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.WaveFile, FilePath, device), () => throw new InvalidOperationException(), default));
        Assert.Equal("close uwsc_audio_playback", platform.Commands[^1]);
        Assert.DoesNotContain("play uwsc_audio_playback", platform.Commands);
    }

    [Fact]
    public void DefaultOutputDoesNotQueryTheDeviceInventory()
    {
        var platform = new Platform();
        new NativeAudioPlaybackBackend(platform).Play(new(AudioPlaybackKind.WaveFile, FilePath), () => { }, default);
        Assert.Equal(0, platform.OutputQueries);
    }

    [Theory]
    [InlineData("relative.wav")]
    [InlineData("C:\\x\" play other.wav")]
    public void UnsafeOrRelativeFileNameDoesNotReachMci(string path)
    {
        var platform = new Platform();
        Assert.Throws<ArgumentException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.WaveFile, path), () => { }, default));
        Assert.Empty(platform.Commands);
    }

    [Fact]
    public void BeepUsesTwoOriginalTonesAndSignalsBeforeCallingTheApi()
    {
        var platform = new Platform();
        bool started = false;
        platform.OnBeep = () => Assert.True(started);
        new NativeAudioPlaybackBackend(platform).Play(new(AudioPlaybackKind.Beep, ""), () => started = true, default);
        Assert.Equal(new[] { (800u, 200u), (800u, 200u) }, platform.Tones);
        Assert.Empty(platform.Commands);
    }

    [Fact]
    public void CancellationBetweenTonesPreventsTheSecondTone()
    {
        var platform = new Platform();
        using var stop = new CancellationTokenSource();
        platform.OnBeep = stop.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.Beep, ""), () => { }, stop.Token));
        Assert.Single(platform.Tones);
    }

    [Fact]
    public void EventFileIsResolvedAndUsesTheWavePlayer()
    {
        var platform = new Platform();
        new NativeAudioPlaybackBackend(platform).Play(new(AudioPlaybackKind.SystemEvent, "SystemAsterisk"), () => { }, default);
        Assert.Equal("SystemAsterisk", platform.Event);
        Assert.Contains("open \"C:\\event.wav\" type waveaudio alias uwsc_audio_playback", platform.Commands);
        Assert.Empty(platform.Tones);
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void EventNameLengthHonorsTheNativeBoundary(int length, bool valid)
    {
        var platform = new Platform();
        var request = new AudioHostPlayRequest(AudioPlaybackKind.SystemEvent, new string('e', length));
        if (valid)
        {
            new NativeAudioPlaybackBackend(platform).Play(request, () => { }, default);
            Assert.Equal(request.Source, platform.Event);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new NativeAudioPlaybackBackend(platform).Play(request, () => { }, default));
            Assert.Null(platform.Event);
        }
    }

    [Theory]
    [InlineData("open", false)]
    [InlineData("play", true)]
    [InlineData("status", true)]
    [InlineData("close", true)]
    public void NativeFailureIsKeptAndOpenedDevicesAreClosed(string stage, bool closes)
    {
        var failure = new AudioPlaybackException(stage, 42);
        var platform = new Platform { FailureStage = stage, Failure = failure };
        Assert.Same(failure, Assert.Throws<AudioPlaybackException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.WaveFile, FilePath), () => { }, default)));
        Assert.Equal(closes, platform.Commands.Contains("close uwsc_audio_playback"));
    }

    [Fact]
    public void PlaybackFailureAndCloseFailureAreBothRetained()
    {
        var primary = new AudioPlaybackException("play failed", 6);
        var cleanup = new AudioPlaybackException("close failed", 7);
        var platform = new Platform { FailureStage = "play", Failure = primary, CloseFailure = cleanup };
        Assert.Same(primary, Assert.Throws<AudioPlaybackException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.WaveFile, FilePath), () => { }, default)));
        Assert.Same(cleanup, primary.Data["音声の解放"]);
    }

    [Fact]
    public void CallbackFailureStillClosesTheDevice()
    {
        var platform = new Platform();
        var failure = new IOException("pipe failed");
        Assert.Same(failure, Assert.Throws<IOException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.WaveFile, FilePath), () => throw failure, default)));
        Assert.Equal("close uwsc_audio_playback", platform.Commands[^1]);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("open")]
    [InlineData("not ready")]
    public void UnexpectedStatusDoesNotReportNormalCompletion(string mode)
    {
        var platform = new Platform { Mode = mode };
        Assert.Throws<AudioPlaybackException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.WaveFile, FilePath), () => { }, default));
        Assert.Equal("close uwsc_audio_playback", platform.Commands[^1]);
    }

    [Fact]
    public void CancellationAfterStartClosesTheDevice()
    {
        var platform = new Platform();
        using var stop = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.WaveFile, FilePath), stop.Cancel, stop.Token));
        Assert.Equal("close uwsc_audio_playback", platform.Commands[^1]);
    }

    [Fact]
    public void VideoOpenFailureStillDestroysTheHiddenOwner()
    {
        var failure = new AudioPlaybackException("open failure", 5);
        var platform = new Platform { FailureStage = "open", Failure = failure };
        Assert.Same(failure, Assert.Throws<AudioPlaybackException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.VideoFile, FilePath), () => { }, default)));
        Assert.Equal(1, platform.DestroyedVideoWindows);
        Assert.DoesNotContain("close uwsc_audio_playback", platform.Commands);
    }

    [Fact]
    public void VideoCloseAndWindowCleanupFailuresBothRemainWithThePrimaryError()
    {
        var failure = new AudioPlaybackException("play failure", 5);
        var close = new IOException("close failure");
        var window = new IOException("window failure");
        var platform = new Platform
        {
            FailureStage = "play",
            Failure = failure,
            CloseFailure = close,
            DestroyFailure = window
        };
        Assert.Same(failure, Assert.Throws<AudioPlaybackException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.VideoFile, FilePath), () => { }, default)));
        AggregateException cleanup = Assert.IsType<AggregateException>(failure.Data["音声の解放"]);
        Assert.Contains(close, cleanup.InnerExceptions);
        Assert.Contains(window, cleanup.InnerExceptions);
        Assert.Equal(1, platform.DestroyedVideoWindows);
    }

    [Fact]
    public void PlayingAndSeekingArePolledUntilStopped()
    {
        var platform = new Platform();
        foreach (string mode in new[] { "playing", "seeking", "stopped" })
        {
            platform.Modes.Enqueue(mode);
        }

        new NativeAudioPlaybackBackend(platform).Play(new(AudioPlaybackKind.WaveFile, FilePath), () => { }, default);
        Assert.Equal(3, platform.Commands.Count(command => command == "status uwsc_audio_playback mode"));
        Assert.Equal(2, platform.Ticks);
    }

    [Theory]
    [InlineData("..\\other")]
    [InlineData("other/path")]
    public void EventNameCannotSelectAnotherRegistryKey(string name)
    {
        var platform = new Platform();
        Assert.Throws<ArgumentException>(() => new NativeAudioPlaybackBackend(platform).Play(
            new(AudioPlaybackKind.SystemEvent, name), () => { }, default));
        Assert.Null(platform.Event);
        Assert.Empty(platform.Commands);
    }

    private sealed class Platform : IAudioNativePlatform
    {
        internal List<string> Commands { get; } = [];
        internal List<int> Threads { get; } = [];
        internal List<(uint, uint)> Tones { get; } = [];
        internal Action? OnBeep
        {
            get; set;
        }
        internal string? Event
        {
            get; private set;
        }
        internal string Mode { get; set; } = "stopped";
        internal string? FailureStage
        {
            get; set;
        }
        internal Exception? Failure
        {
            get; set;
        }
        internal Exception? CloseFailure
        {
            get; set;
        }
        internal Exception? DestroyFailure
        {
            get; set;
        }
        internal int OutputQueries
        {
            get; private set;
        }
        internal int DestroyedVideoWindows
        {
            get; private set;
        }
        internal Queue<string> Modes { get; } = new();
        internal int Ticks
        {
            get; private set;
        }

        public uint WaveOutputCount
        {
            get
            {
                OutputQueries++;
                return 2;
            }
        }

        public string Mci(string command)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Commands.Add(command);
            if (command.StartsWith("close ", StringComparison.Ordinal) && CloseFailure is not null)
            {
                throw CloseFailure;
            }

            if (FailureStage is not null && command.StartsWith(FailureStage + " ", StringComparison.Ordinal))
            {
                throw Failure!;
            }

            return command.StartsWith("status ", StringComparison.Ordinal)
                ? Modes.Count > 0 ? Modes.Dequeue() : Mode : "";
        }

        public void Beep(uint frequency, uint milliseconds)
        {
            Tones.Add((frequency, milliseconds));
            OnBeep?.Invoke();
        }

        public string EventFile(string name)
        {
            Event = name;
            return @"C:\event.wav";
        }

        public nint CreateVideoWindow()
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            return 123;
        }

        public void DestroyVideoWindow(nint window)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            DestroyedVideoWindows++;
            if (DestroyFailure is not null)
            {
                throw DestroyFailure;
            }
        }

        public void WaitForTick(CancellationToken token)
        {
            Ticks++;
            token.ThrowIfCancellationRequested();
        }
    }
}
