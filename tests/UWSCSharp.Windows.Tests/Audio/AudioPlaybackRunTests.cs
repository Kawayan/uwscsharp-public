using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 偽の再生と入力だけで、セッション間の共有を確認します。画面・音声API・実プロセスは使いません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AudioPlaybackRunTests
{
    private static readonly AudioHostPlayRequest Request = new(AudioPlaybackKind.Beep, "");

    [Fact]
    public async Task WorkerExitKeepsPlaybackOwnedByRoot()
    {
        var factory = new Factory();
        await using IWindowsAutomationSession root = Create(factory);
        IWindowsAutomationSession worker = root.Fork();
        AudioPlaybackTicket ticket = Audio(worker).PlayAudio(Request);
        Playback playback = await factory.NextAsync();
        playback.Start();
        await worker.DisposeAsync();
        Assert.Equal(0, playback.Disposals);
        Assert.False(ticket.Closed.IsCompleted);
        await Bounded(Audio(root).StopAudioAsync());
        Assert.Equal(1, playback.Disposals);
        await ticket.Closed;
    }

    [Fact]
    public async Task RootWorkerAndNestedWorkerShareGenerationsAndStop()
    {
        var factory = new Factory();
        await using IWindowsAutomationSession root = Create(factory);
        await using IWindowsAutomationSession worker = root.Fork();
        await using IWindowsAutomationSession nested = worker.Fork();
        AudioPlaybackTicket first = Audio(root).PlayAudio(Request);
        Playback old = await factory.NextAsync();
        old.Start();
        AudioPlaybackTicket next = Audio(worker).PlayAudio(Request);
        Playback current = await factory.NextAsync();
        current.Start();
        await Bounded(first.Closed);
        Assert.True(next.Generation > first.Generation);
        Assert.Equal(1, old.Disposals);
        await Bounded(Audio(nested).ReleaseAudioAsync(first));
        Assert.Equal(0, current.Disposals);
        await Bounded(Audio(nested).StopAudioAsync());
        Assert.Equal(1, current.Disposals);
        await next.Closed;
    }

    [Fact]
    public async Task NaturalReleaseAllowsNextSessionToPlay()
    {
        var factory = new Factory();
        await using IWindowsAutomationSession root = Create(factory);
        await using IWindowsAutomationSession worker = root.Fork();
        AudioPlaybackTicket first = Audio(worker).PlayAudio(Request);
        Playback playback = await factory.NextAsync();
        playback.Start();
        playback.Complete();
        await Bounded(first.Closed);
        Assert.Equal(1, playback.Disposals);
        AudioPlaybackTicket second = Audio(root).PlayAudio(Request);
        await Bounded(second.Launched);
        await Bounded(Audio(worker).StopAudioAsync());
    }

    [Fact]
    public async Task RootDisposalUnblocksPlaybackWaitBeforeWaitingForWorker()
    {
        var factory = new Factory();
        IWindowsAutomationSession root = Create(factory);
        IWindowsAutomationSession worker = root.Fork();
        try
        {
            AudioPlaybackTicket ticket = Audio(worker).PlayAudio(Request);
            Playback playback = await factory.NextAsync();
            playback.Start();
            var waiting = Task.Run(async () =>
            {
                try
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ticket.Completed);
                }
                finally
                {
                    await worker.DisposeAsync();
                }
            });
            await Bounded(root.DisposeAsync().AsTask());
            await Bounded(waiting);
            Assert.Equal(1, playback.Disposals);
            await ticket.Closed;
        }
        finally
        {
            await worker.DisposeAsync();
            await root.DisposeAsync();
        }
    }

    [Fact]
    public async Task RootDisposalWaitsForForkAfterReleasingPlayback()
    {
        var factory = new Factory();
        IWindowsAutomationSession root = Create(factory);
        IWindowsAutomationSession worker = root.Fork();
        try
        {
            AudioPlaybackTicket ticket = Audio(worker).PlayAudio(Request);
            Playback playback = await factory.NextAsync();
            Task disposal = root.DisposeAsync().AsTask();
            await Bounded(ticket.Closed);
            Assert.Equal(1, playback.Disposals);
            Assert.False(disposal.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => Audio(worker).PlayAudio(Request));
            await worker.DisposeAsync();
            await Bounded(disposal);
        }
        finally
        {
            await worker.DisposeAsync();
            await root.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkerCancellationDoesNotCancelSharedPlayback()
    {
        var factory = new Factory();
        using var rootStop = new CancellationTokenSource();
        using var workerStop = new CancellationTokenSource();
        await using IWindowsAutomationSession root = Create(factory);
        root.ConfigurePause(null, rootStop.Token);
        IWindowsAutomationSession worker = root.Fork();
        worker.ConfigurePause(null, workerStop.Token);
        AudioPlaybackTicket ticket = Audio(worker).PlayAudio(Request);
        Playback playback = await factory.NextAsync();
        await workerStop.CancelAsync();
        await worker.DisposeAsync();
        Assert.Equal(0, playback.Disposals);
        Assert.False(ticket.Closed.IsCompleted);
        await Bounded(Audio(root).StopAudioAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootTokenReconfigurationRetainsBothCancellationSources(bool cancelFirst)
    {
        var factory = new Factory();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        await using IWindowsAutomationSession root = Create(factory);
        root.ConfigurePause(null, first.Token);
        await using IWindowsAutomationSession worker = root.Fork();
        worker.ConfigurePause(null, CancellationToken.None);
        AudioPlaybackTicket ticket = Audio(worker).PlayAudio(Request);
        Playback playback = await factory.NextAsync();
        root.ConfigurePause(null, second.Token);
        root.ConfigurePause(null, second.Token);
        await (cancelFirst ? first : second).CancelAsync();
        await Bounded(ticket.Closed);
        Assert.Equal(1, playback.Disposals);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ticket.Completed);
        Assert.ThrowsAny<OperationCanceledException>(() => Audio(worker).PlayAudio(Request));
    }

    [Fact]
    public async Task AlreadyCancelledRootDoesNotLaunchFactory()
    {
        var factory = new Factory();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await using IWindowsAutomationSession root = Create(factory);
        root.ConfigurePause(null, stop.Token);
        Assert.ThrowsAny<OperationCanceledException>(() => Audio(root).PlayAudio(Request));
        Assert.Equal(0, factory.Starts);
        await Audio(root).StopAudioAsync();
    }

    [Fact]
    public async Task CancelledRequestDoesNotReplaceExistingPlayback()
    {
        var factory = new Factory();
        await using IWindowsAutomationSession root = Create(factory);
        AudioPlaybackTicket ticket = Audio(root).PlayAudio(Request);
        Playback playback = await factory.NextAsync();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Audio(root).PlayAudio(Request, stop.Token));
        Assert.Equal(1, factory.Starts);
        Assert.Equal(0, playback.Disposals);
        Assert.False(ticket.Closed.IsCompleted);
        await Bounded(Audio(root).StopAudioAsync());
    }

    [Fact]
    public async Task ExplicitStopAllowsLaterPlayback()
    {
        var factory = new Factory();
        await using IWindowsAutomationSession root = Create(factory);
        AudioPlaybackTicket first = Audio(root).PlayAudio(Request);
        await Bounded(first.Launched);
        await Bounded(Audio(root).StopAudioAsync());
        AudioPlaybackTicket next = Audio(root).PlayAudio(Request);
        await Bounded(next.Launched);
        Assert.Equal(2, factory.Starts);
        await Bounded(Audio(root).ReleaseAudioAsync(next));
    }

    [Fact]
    public async Task DifferentRunsKeepOwnershipAndCancellationIndependent()
    {
        var factory = new Factory();
        IWindowsAutomationSession first = Create(factory);
        await using IWindowsAutomationSession second = Create(factory);
        try
        {
            AudioPlaybackTicket a = Audio(first).PlayAudio(Request);
            Playback one = await factory.NextAsync();
            AudioPlaybackTicket b = Audio(second).PlayAudio(Request);
            Playback two = await factory.NextAsync();
            Assert.Equal(a.Generation, b.Generation);
            await Assert.ThrowsAsync<ArgumentException>(() => Audio(first).ReleaseAudioAsync(b));
            await Bounded(first.DisposeAsync().AsTask());
            Assert.Equal(1, one.Disposals);
            Assert.Equal(0, two.Disposals);
            Assert.False(b.Closed.IsCompleted);
            await Bounded(Audio(second).StopAudioAsync());
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposedWorkerCannotControlRootPlayback()
    {
        var factory = new Factory();
        await using IWindowsAutomationSession root = Create(factory);
        IWindowsAutomationSession worker = root.Fork();
        IAudioPlaybackSession endpoint = Audio(worker);
        AudioPlaybackTicket ticket = endpoint.PlayAudio(Request);
        Playback playback = await factory.NextAsync();
        await worker.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => endpoint.PlayAudio(Request));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => endpoint.StopAudioAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => endpoint.ReleaseAudioAsync(ticket));
        Assert.Equal(0, playback.Disposals);
        await Bounded(Audio(root).StopAudioAsync());
    }

    [Fact]
    public async Task NoPlaybackDoesNotLaunchFactory()
    {
        var factory = new Factory();
        using var stop = new CancellationTokenSource();
        IWindowsAutomationSession root = Create(factory);
        root.ConfigurePause(null, stop.Token);
        await Audio(root).StopAudioAsync();
        await root.DisposeAsync();
        await stop.CancelAsync();
        Assert.Equal(0, factory.Starts);
    }

    [Fact]
    public async Task CancellationCallbackFailureDoesNotSkipPlaybackDisposal()
    {
        var factory = new Factory();
        var failure = new IOException("取消コールバックの失敗");
        factory.OnStart = token => token.Register(() => throw failure);
        using var stop = new CancellationTokenSource();
        IWindowsAutomationSession root = Create(factory);
        root.ConfigurePause(null, stop.Token);
        AudioPlaybackTicket ticket = Audio(root).PlayAudio(Request);
        Playback playback = await factory.NextAsync();
        await stop.CancelAsync();
        await Bounded(ticket.Closed);
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(
            () => Bounded(root.DisposeAsync().AsTask()));
        Assert.Contains(failure, error.Flatten().InnerExceptions);
        Assert.Equal(1, playback.Disposals);
        await root.DisposeAsync();
    }

    [Fact]
    public async Task AudioFailureStillWaitsForWorkerAndDisposesOtherResources()
    {
        var failure = new IOException("再生資源の解放失敗");
        var factory = new Factory { DisposalFailure = failure };
        var touch = new Touch();
        IWindowsAutomationSession root = Create(factory, touch);
        IWindowsAutomationSession worker = root.Fork();
        try
        {
            AudioPlaybackTicket ticket = Audio(worker).PlayAudio(Request);
            Playback playback = await factory.NextAsync();
            Task disposal = root.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<AggregateException>(() => Bounded(ticket.Closed));
            Assert.Equal(1, playback.Disposals);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, touch.Disposals);
            await worker.DisposeAsync();
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => Bounded(disposal));
            Assert.Contains(failure, error.Flatten().InnerExceptions);
            Assert.Equal(1, touch.Disposals);
        }
        finally
        {
            await worker.DisposeAsync();
            await root.DisposeAsync();
        }
    }

    private static IAudioPlaybackSession Audio(IWindowsAutomationSession session)
        => Assert.IsAssignableFrom<IAudioPlaybackSession>(session);

    private static IWindowsAutomationSession Create(Factory factory, Touch? touch = null)
        => new WindowsAutomation(_ => throw new InvalidOperationException("物理入力は禁止です。"),
            touchBackend: touch, swappedMouseButtons: false,
            audioPlaybackFactory: factory.Start).CreateSession();

    private static Task Bounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(8));

    private sealed class Factory
    {
        private readonly System.Threading.Channels.Channel<Playback> created =
            System.Threading.Channels.Channel.CreateUnbounded<Playback>();
        private int starts;

        internal Action<CancellationToken>? OnStart
        {
            get; set;
        }

        internal Exception? DisposalFailure
        {
            get; set;
        }

        internal int Starts => Volatile.Read(ref starts);

        internal IAudioPlaybackOperation Start(AudioHostPlayRequest request, CancellationToken token)
        {
            OnStart?.Invoke(token);
            var playback = new Playback(token, DisposalFailure);
            Interlocked.Increment(ref starts);
            Assert.True(created.Writer.TryWrite(playback));
            return playback;
        }

        internal Task<Playback> NextAsync()
            => created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
    }

    private sealed class Playback : IAudioPlaybackOperation
    {
        private readonly CancellationToken token;
        private readonly Exception? disposalFailure;
        private readonly TaskCompletionSource<AudioHostMessage> started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<AudioHostMessage> completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposals;

        internal Playback(CancellationToken token, Exception? disposalFailure)
        {
            this.token = token;
            this.disposalFailure = disposalFailure;
            Signals = new(Task.FromResult(1u), started.Task, completed.Task, closed.Task);
        }

        public AudioPlaybackSignals Signals
        {
            get;
        }

        internal int Disposals => Volatile.Read(ref disposals);

        internal void Start() => started.TrySetResult(new(AudioHostMessageType.Started));

        internal void Complete() => completed.TrySetResult(new(AudioHostMessageType.Completed));

        public Task FinishAsync(CancellationToken token) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposals);
            started.TrySetCanceled(token);
            completed.TrySetCanceled(token);
            if (disposalFailure is not null)
            {
                closed.TrySetException(disposalFailure);
                return ValueTask.FromException(disposalFailure);
            }

            closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Touch : ITouchInputBackend
    {
        internal int Disposals
        {
            get; private set;
        }

        public void ValidatePosition(Native.Point position) => throw new InvalidOperationException("タッチ入力は禁止です。");

        public void Send(IReadOnlyList<TouchContact> contacts) => throw new InvalidOperationException("タッチ入力は禁止です。");

        public void Dispose() => Disposals++;
    }
}
