using System.Collections.Concurrent;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 偽の再生処理とTaskだけで所有権を検査し、音声API・画面・プロセスを使用しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AudioPlaybackOwnerTests
{
    private static readonly AudioHostPlayRequest Request = new(AudioPlaybackKind.Beep, "");

    [Fact]
    public async Task StopDuringStartWaitDisposesBackend()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            created.TrySetResult(playback);
            return playback;
        });
        AudioPlaybackTicket ticket = owner.Replace(Request);
        FakePlayback playback = await Bounded(created.Task);
        Assert.False(ticket.Started.IsCompleted);
        await Bounded(owner.StopAsync());
        Assert.Equal(1, playback.DisposeCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ticket.Started);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ticket.Completed);
        await ticket.Closed;
    }

    [Fact]
    public async Task ReplacementWaitsForPreviousCleanupAndSkipsCancelledQueue()
    {
        var created = new ConcurrentQueue<FakePlayback>();
        TaskCompletionSource<FakePlayback> firstCreated = NewSignal<FakePlayback>();
        TaskCompletionSource<FakePlayback> secondCreated = NewSignal<FakePlayback>();
        TaskCompletionSource release = NewSignal();
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            created.Enqueue(playback);
            if (created.Count == 1)
            {
                playback.DisposeGate = release.Task;
                firstCreated.TrySetResult(playback);
            }
            else
            {
                secondCreated.TrySetResult(playback);
            }

            return playback;
        });
        try
        {
            AudioPlaybackTicket first = owner.Replace(Request);
            FakePlayback previous = await Bounded(firstCreated.Task);
            previous.Start();
            await Bounded(first.Started);
            AudioPlaybackTicket skipped = owner.Replace(Request);
            await Bounded(previous.Disposing.Task);
            AudioPlaybackTicket next = owner.Replace(Request);
            Assert.True(first.Generation < skipped.Generation && skipped.Generation < next.Generation);
            Assert.Single(created);
            Assert.False(next.Launched.IsCompleted);
            release.TrySetResult();
            FakePlayback current = await Bounded(secondCreated.Task);
            Assert.Equal(2, created.Count);
            await first.Closed;
            await skipped.Closed;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => skipped.Launched);
            current.Start();
            Assert.Equal(AudioHostMessageType.Started, (await Bounded(next.Started)).Type);
            await Bounded(owner.StopAsync());
            Assert.Equal(1, previous.DisposeCount);
            Assert.Equal(1, current.DisposeCount);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task StopQueuedRequestStillWaitsForEarlierCleanup()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        TaskCompletionSource release = NewSignal();
        int count = 0;
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            Interlocked.Increment(ref count);
            var playback = new FakePlayback(token) { DisposeGate = release.Task };
            created.TrySetResult(playback);
            return playback;
        });
        try
        {
            AudioPlaybackTicket first = owner.Replace(Request);
            FakePlayback playback = await Bounded(created.Task);
            AudioPlaybackTicket queued = owner.Replace(Request);
            await Bounded(playback.Disposing.Task);
            Task stopping = owner.StopAsync();
            Assert.Same(stopping, owner.StopAsync());
            Assert.False(stopping.IsCompleted);
            Assert.False(queued.Closed.IsCompleted);
            release.TrySetResult();
            await Bounded(stopping);
            Assert.Equal(1, count);
            await first.Closed;
            await queued.Closed;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Started);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task LatePreviousNotificationCannotCompleteNextTicket()
    {
        TaskCompletionSource<FakePlayback> firstCreated = NewSignal<FakePlayback>();
        TaskCompletionSource<FakePlayback> nextCreated = NewSignal<FakePlayback>();
        int count = 0;
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token) { PreserveNotificationsOnDispose = true };
            (Interlocked.Increment(ref count) == 1 ? firstCreated : nextCreated).TrySetResult(playback);
            return playback;
        });
        AudioPlaybackTicket old = owner.Replace(Request);
        FakePlayback previous = await Bounded(firstCreated.Task);
        AudioPlaybackTicket current = owner.Replace(Request);
        FakePlayback next = await Bounded(nextCreated.Task);
        await old.Closed;
        previous.Start();
        previous.Complete();
        await Bounded(owner.ReleaseAsync(old));
        Assert.False(current.Started.IsCompleted);
        Assert.False(current.Completed.IsCompleted);
        next.Start();
        Assert.Equal(0, next.DisposeCount);
        next.Complete();
        await Bounded(current.Completed);
        await Bounded(current.Closed);
        Assert.Equal(1, next.DisposeCount);
        await Bounded(owner.StopAsync());
    }

    [Fact]
    public async Task TerminalNotificationReleasesWithoutExplicitStop()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            created.TrySetResult(playback);
            return playback;
        });
        AudioPlaybackTicket ticket = owner.Replace(Request);
        FakePlayback playback = await Bounded(created.Task);
        playback.Start();
        playback.Complete();
        Assert.Equal(AudioHostMessageType.Completed, (await Bounded(ticket.Completed)).Type);
        await Bounded(ticket.Closed);
        Assert.Equal(1, playback.DisposeCount);
    }

    [Fact]
    public async Task NaturalBackendCloseAllowsNextRequest()
    {
        var queue = new ConcurrentQueue<FakePlayback>();
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            queue.Enqueue(playback);
            created.TrySetResult(playback);
            return playback;
        });
        AudioPlaybackTicket first = owner.Replace(Request);
        FakePlayback playback = await Bounded(created.Task);
        playback.Start();
        playback.Complete();
        playback.Close();
        await Bounded(first.Closed);
        AudioPlaybackTicket next = owner.Replace(Request);
        await Bounded(next.Launched);
        Assert.Equal(2, queue.Count);
        await Bounded(owner.StopAsync());
    }

    [Fact]
    public async Task TerminalCleanupKeepsFollowingRequestQueued()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        TaskCompletionSource release = NewSignal();
        int starts = 0;
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            if (Interlocked.Increment(ref starts) == 1)
            {
                playback.DisposeGate = release.Task;
                created.TrySetResult(playback);
            }

            return playback;
        });
        try
        {
            AudioPlaybackTicket first = owner.Replace(Request);
            FakePlayback playback = await Bounded(created.Task);
            playback.Start();
            playback.Complete();
            await Bounded(playback.Disposing.Task);
            AudioPlaybackTicket next = owner.Replace(Request);
            Assert.False(first.Closed.IsCompleted);
            Assert.False(next.Launched.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref starts));
            release.TrySetResult();
            await Bounded(first.Closed);
            await Bounded(next.Launched);
            Assert.Equal(2, Volatile.Read(ref starts));
            await Bounded(owner.StopAsync());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task TerminalCleanupFailureIsRetainedAndBlocksNextRequest()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        var failure = new IOException("自然終了後の解放失敗");
        var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token) { DisposeFailure = failure };
            created.TrySetResult(playback);
            return playback;
        });
        try
        {
            AudioPlaybackTicket first = owner.Replace(Request);
            FakePlayback playback = await Bounded(created.Task);
            playback.Start();
            playback.Complete();
            Assert.Equal(AudioHostMessageType.Completed, (await Bounded(first.Completed)).Type);
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(
                () => Bounded(first.Closed));
            Assert.Contains(failure, error.Flatten().InnerExceptions);
            AudioPlaybackTicket next = owner.Replace(Request);
            await Assert.ThrowsAsync<AggregateException>(() => Bounded(next.Closed));
            _ = await Record.ExceptionAsync(() => next.Launched);
            _ = await Record.ExceptionAsync(() => next.Started);
            _ = await Record.ExceptionAsync(() => next.Completed);
            Assert.Equal(1, playback.DisposeCount);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task TerminalWithoutStartedNotificationFailsAndStillDisposes()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            created.TrySetResult(playback);
            return playback;
        });
        try
        {
            AudioPlaybackTicket ticket = owner.Replace(Request);
            FakePlayback playback = await Bounded(created.Task);
            playback.Complete();
            await Assert.ThrowsAsync<InvalidDataException>(() => Bounded(ticket.Closed));
            await Assert.ThrowsAsync<InvalidDataException>(() => ticket.Started);
            Assert.Equal(AudioHostMessageType.Completed, (await ticket.Completed).Type);
            Assert.Equal(1, playback.DisposeCount);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task TerminalNotificationDoesNotHideExitFailure()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        var failure = new InvalidDataException("終了コード7");
        var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token) { FinishFailure = failure };
            created.TrySetResult(playback);
            return playback;
        });
        try
        {
            AudioPlaybackTicket ticket = owner.Replace(Request);
            FakePlayback playback = await Bounded(created.Task);
            playback.Start();
            playback.Complete();
            Assert.Equal(AudioHostMessageType.Completed, (await Bounded(ticket.Completed)).Type);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(
                () => Bounded(ticket.Closed)));
            Assert.Equal(1, playback.DisposeCount);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task StopDuringTerminalExitWaitStillDisposesAndKeepsNotification()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token) { FinishGate = NewSignal().Task };
            created.TrySetResult(playback);
            return playback;
        });
        AudioPlaybackTicket ticket = owner.Replace(Request);
        FakePlayback playback = await Bounded(created.Task);
        playback.Start();
        playback.Complete();
        await Bounded(playback.Finishing.Task);
        Assert.False(ticket.Closed.IsCompleted);
        await Bounded(owner.StopAsync());
        Assert.Equal(AudioHostMessageType.Completed, (await ticket.Completed).Type);
        Assert.Equal(1, playback.DisposeCount);
        await ticket.Closed;
    }

    [Fact]
    public async Task ExplicitStopDoesNotCloseOwner()
    {
        await using var owner = new AudioPlaybackOwner((_, token) => new FakePlayback(token));
        AudioPlaybackTicket first = owner.Replace(Request);
        await Bounded(first.Launched);
        await Bounded(owner.ReleaseAsync(first));
        AudioPlaybackTicket next = owner.Replace(Request);
        await Bounded(next.Launched);
        Assert.True(next.Generation > first.Generation);
        await Bounded(owner.StopAsync());
    }

    [Fact]
    public async Task RootCancellationPreservesRootToken()
    {
        using var cancellation = new CancellationTokenSource();
        await using var owner = new AudioPlaybackOwner((_, token) => new FakePlayback(token), cancellation.Token);
        AudioPlaybackTicket ticket = owner.Replace(Request);
        await Bounded(ticket.Launched);
        cancellation.Cancel();
        await Bounded(ticket.Closed);
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ticket.Started);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.ThrowsAny<OperationCanceledException>(() => owner.Replace(Request));
    }

    [Fact]
    public async Task DisposeDuringFactoryWaitKeepsOwnershipUntilReturned()
    {
        TaskCompletionSource entered = NewSignal();
        TaskCompletionSource release = NewSignal();
        FakePlayback? playback = null;
        var owner = new AudioPlaybackOwner((_, token) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            playback = new FakePlayback(token);
            return playback;
        });
        try
        {
            AudioPlaybackTicket ticket = owner.Replace(Request);
            await Bounded(entered.Task);
            Task disposing = owner.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            release.TrySetResult();
            await Bounded(disposing);
            await ticket.Closed;
            Assert.Equal(1, playback!.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => owner.Replace(Request));
        }
        finally
        {
            release.TrySetResult();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvalidRequestDoesNotStopCurrentPlayback()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            created.TrySetResult(playback);
            return playback;
        });
        AudioPlaybackTicket ticket = owner.Replace(Request);
        FakePlayback playback = await Bounded(created.Task);
        playback.Start();
        await Bounded(ticket.Started);
        Assert.Throws<InvalidDataException>(() => owner.Replace(new(AudioPlaybackKind.Beep, "invalid")));
        Assert.Equal(0, playback.DisposeCount);
        Assert.False(ticket.Closed.IsCompleted);
        await Bounded(owner.StopAsync());
    }

    [Fact]
    public async Task CleanupFailurePreventsFollowingFactoryAndIsPreserved()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        var failure = new IOException("偽の解放失敗");
        int count = 0;
        var owner = new AudioPlaybackOwner((_, token) =>
        {
            Interlocked.Increment(ref count);
            var playback = new FakePlayback(token) { DisposeFailure = failure };
            created.TrySetResult(playback);
            return playback;
        });
        try
        {
            AudioPlaybackTicket first = owner.Replace(Request);
            await Bounded(created.Task);
            AudioPlaybackTicket next = owner.Replace(Request);
            Exception? error = await Record.ExceptionAsync(() => Bounded(next.Closed));
            Assert.IsType<AggregateException>(error);
            Assert.Contains(failure, ((AggregateException)error).Flatten().InnerExceptions);
            Assert.Equal(1, count);
            _ = await Record.ExceptionAsync(() => first.Started);
            _ = await Record.ExceptionAsync(() => first.Completed);
            _ = await Record.ExceptionAsync(() => first.Closed);
            _ = await Record.ExceptionAsync(() => next.Launched);
            _ = await Record.ExceptionAsync(() => next.Started);
            _ = await Record.ExceptionAsync(() => next.Completed);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task MissingNotificationFailsInsteadOfBecomingCancellation()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            created.TrySetResult(playback);
            return playback;
        });
        try
        {
            AudioPlaybackTicket ticket = owner.Replace(Request);
            FakePlayback playback = await Bounded(created.Task);
            playback.Close();
            await Assert.ThrowsAsync<InvalidDataException>(() => Bounded(ticket.Closed));
            await Assert.ThrowsAsync<InvalidDataException>(() => ticket.Started);
            await Assert.ThrowsAsync<InvalidDataException>(() => ticket.Completed);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task ConcurrentReplacementNeverOverlapsLiveBackends()
    {
        int active = 0;
        int maximum = 0;
        var tickets = new ConcurrentBag<AudioPlaybackTicket>();
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            int current = Interlocked.Increment(ref active);
            int previous;
            do
            {
                previous = Volatile.Read(ref maximum);
            }
            while (current > previous && Interlocked.CompareExchange(ref maximum, current, previous) != previous);
            return new FakePlayback(token, () => Interlocked.Decrement(ref active));
        });
        AudioPlaybackTicket seed = owner.Replace(Request);
        await Bounded(seed.Launched);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => tickets.Add(owner.Replace(Request)))));
        await Bounded(owner.StopAsync());
        Assert.Equal(0, active);
        Assert.Equal(1, maximum);
        Assert.Equal(40, tickets.Select(ticket => ticket.Generation).Distinct().Count());
        Assert.All(tickets, ticket => Assert.True(ticket.Closed.IsCompletedSuccessfully));
    }

    [Fact]
    public async Task FactoryFailureRemainsFatalWithoutStartingAnotherBackend()
    {
        var failure = new IOException("偽の起動失敗");
        int count = 0;
        var owner = new AudioPlaybackOwner((_, _) =>
        {
            Interlocked.Increment(ref count);
            throw failure;
        });
        try
        {
            AudioPlaybackTicket first = owner.Replace(Request);
            Assert.Same(failure, await Record.ExceptionAsync(() => Bounded(first.Closed)));
            Assert.Same(failure, await Record.ExceptionAsync(() => first.Launched));
            Assert.Same(failure, await Record.ExceptionAsync(() => first.Started));
            Assert.Same(failure, await Record.ExceptionAsync(() => first.Completed));
            AudioPlaybackTicket next = owner.Replace(Request);
            await Assert.ThrowsAsync<AggregateException>(() => Bounded(next.Closed));
            Assert.Equal(1, count);
            _ = await Record.ExceptionAsync(() => next.Launched);
            _ = await Record.ExceptionAsync(() => next.Started);
            _ = await Record.ExceptionAsync(() => next.Completed);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task PrimaryFailureKeepsAdditionalCleanupFailure()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        var primary = new IOException("偽の通信失敗");
        var cleanup = new IOException("偽の解放失敗");
        var owner = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token) { DisposeFailure = cleanup };
            created.TrySetResult(playback);
            return playback;
        });
        try
        {
            AudioPlaybackTicket ticket = owner.Replace(Request);
            FakePlayback playback = await Bounded(created.Task);
            playback.Start();
            playback.Fail(primary);
            Assert.Same(primary, await Record.ExceptionAsync(() => Bounded(ticket.Closed)));
            AggregateException additional = Assert.IsType<AggregateException>(primary.Data["再生の解放"]);
            Assert.Contains(cleanup, additional.Flatten().InnerExceptions);
            Assert.Equal(AudioHostMessageType.Started, (await Bounded(ticket.Started)).Type);
            _ = await Record.ExceptionAsync(() => ticket.Completed);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task CancellationCallbackFailureDoesNotSkipDisposal()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        var failure = new IOException("偽の取消通知失敗");
        CancellationTokenRegistration registration = default;
        var owner = new AudioPlaybackOwner((_, token) =>
        {
            registration = token.Register(() => throw failure);
            var playback = new FakePlayback(token);
            created.TrySetResult(playback);
            return playback;
        });
        try
        {
            AudioPlaybackTicket ticket = owner.Replace(Request);
            FakePlayback playback = await Bounded(created.Task);
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => Bounded(owner.StopAsync()));
            Assert.Contains(failure, error.Flatten().InnerExceptions);
            Assert.Equal(1, playback.DisposeCount);
            _ = await Record.ExceptionAsync(() => ticket.Closed);
            _ = await Record.ExceptionAsync(() => ticket.Started);
            _ = await Record.ExceptionAsync(() => ticket.Completed);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
            registration.Dispose();
        }
    }

    [Fact]
    public async Task ReportedFailureDoesNotPoisonNextRequest()
    {
        TaskCompletionSource<FakePlayback> created = NewSignal<FakePlayback>();
        int count = 0;
        await using var owner = new AudioPlaybackOwner((_, token) =>
        {
            Interlocked.Increment(ref count);
            var playback = new FakePlayback(token);
            created.TrySetResult(playback);
            return playback;
        });
        AudioPlaybackTicket first = owner.Replace(Request);
        FakePlayback playback = await Bounded(created.Task);
        playback.ReportFailure();
        Assert.Equal(AudioHostMessageType.Failed, (await Bounded(first.Started)).Type);
        Assert.Equal(5u, (await Bounded(first.Completed)).Failure!.NativeCode);
        await Bounded(first.Closed);
        AudioPlaybackTicket next = owner.Replace(Request);
        await Bounded(next.Launched);
        Assert.Equal(2, count);
        await Bounded(owner.StopAsync());
    }

    [Fact]
    public async Task EmptyOwnerCanBeDisposedRepeatedly()
    {
        int count = 0;
        var owner = new AudioPlaybackOwner((_, token) =>
        {
            Interlocked.Increment(ref count);
            return new FakePlayback(token);
        });
        await owner.StopAsync();
        await Task.WhenAll(owner.DisposeAsync().AsTask(), owner.DisposeAsync().AsTask());
        await owner.StopAsync();
        Assert.Equal(0, count);
        Assert.Throws<ObjectDisposedException>(() => owner.Replace(Request));
    }

    [Fact]
    public async Task ForeignTicketCannotReleaseEitherOwner()
    {
        TaskCompletionSource<FakePlayback> firstCreated = NewSignal<FakePlayback>();
        TaskCompletionSource<FakePlayback> secondCreated = NewSignal<FakePlayback>();
        await using var first = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            firstCreated.TrySetResult(playback);
            return playback;
        });
        await using var second = new AudioPlaybackOwner((_, token) =>
        {
            var playback = new FakePlayback(token);
            secondCreated.TrySetResult(playback);
            return playback;
        });
        AudioPlaybackTicket a = first.Replace(Request);
        AudioPlaybackTicket b = second.Replace(Request);
        FakePlayback one = await Bounded(firstCreated.Task);
        FakePlayback two = await Bounded(secondCreated.Task);
        Assert.Equal(a.Generation, b.Generation);
        await Assert.ThrowsAsync<ArgumentException>(() => first.ReleaseAsync(b));
        Assert.Equal(0, one.DisposeCount);
        Assert.Equal(0, two.DisposeCount);
        await Bounded(first.ReleaseAsync(a));
        await Bounded(second.ReleaseAsync(b));
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Bounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    private static Task<T> Bounded<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    private sealed class FakePlayback : IAudioPlaybackOperation
    {
        private readonly object gate = new();
        private readonly CancellationToken token;
        private readonly Action? released;
        private readonly TaskCompletionSource<AudioHostMessage> started = NewSignal<AudioHostMessage>();
        private readonly TaskCompletionSource<AudioHostMessage> completed = NewSignal<AudioHostMessage>();
        private readonly TaskCompletionSource closed = NewSignal();
        private Task? disposal;
        private int disposeCount;

        internal FakePlayback(CancellationToken token, Action? released = null)
        {
            this.token = token;
            this.released = released;
            Signals = new(Task.FromResult(1u), started.Task, completed.Task, closed.Task);
        }

        public AudioPlaybackSignals Signals
        {
            get;
        }

        internal Task DisposeGate { get; set; } = Task.CompletedTask;

        internal Exception? FinishFailure
        {
            get; set;
        }

        public async Task FinishAsync(CancellationToken token)
        {
            Finishing.TrySetResult();
            await FinishGate.WaitAsync(token);
            if (FinishFailure is not null)
            {
                closed.TrySetException(FinishFailure);
                throw FinishFailure;
            }
        }

        internal Task FinishGate { get; set; } = Task.CompletedTask;

        internal TaskCompletionSource Finishing { get; } = NewSignal();

        internal Exception? DisposeFailure
        {
            get; set;
        }

        internal bool PreserveNotificationsOnDispose
        {
            get; set;
        }

        internal TaskCompletionSource Disposing { get; } = NewSignal();

        internal int DisposeCount => Volatile.Read(ref disposeCount);

        internal void Start() => started.TrySetResult(new(AudioHostMessageType.Started));

        internal void Complete() => completed.TrySetResult(new(AudioHostMessageType.Completed));

        internal void Close() => closed.TrySetResult();

        internal void Fail(Exception failure)
        {
            started.TrySetException(failure);
            completed.TrySetException(failure);
            closed.TrySetException(failure);
        }

        internal void ReportFailure()
        {
            var failure = new AudioHostMessage(AudioHostMessageType.Failed,
                Failure: new("偽の起動失敗", 5));
            started.TrySetResult(failure);
            completed.TrySetResult(failure);
            closed.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                return new(disposal ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            Interlocked.Increment(ref disposeCount);
            Disposing.TrySetResult();
            await DisposeGate;
            if (!PreserveNotificationsOnDispose)
            {
                started.TrySetCanceled(token);
                completed.TrySetCanceled(token);
            }

            released?.Invoke();
            if (DisposeFailure is not null)
            {
                closed.TrySetException(DisposeFailure);
                throw DisposeFailure;
            }

            closed.TrySetResult();
        }
    }
}
