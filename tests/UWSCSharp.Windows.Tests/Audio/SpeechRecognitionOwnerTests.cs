using System.Runtime.Versioning;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽の認識sessionだけで所有処理を確認し、認識器・音声入力・画面を使用しません。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionOwnerTests
{
    [Fact]
    public async Task StopAndReadBeforeStartDoNotCreateEngine()
    {
        var fixture = new Fixture();
        await using var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        await owner.StopAsync();
        Assert.Equal(string.Empty, owner.TakeLatest());
        using var cancellation = new CancellationTokenSource();
        Task<string> waiting = owner.WaitForNextAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, fixture.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartClearsAfterStoppingAndBeforeNewActivation(bool words)
    {
        var fixture = new Fixture();
        await using var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        await owner.StartDictationAsync();
        fixture.Receive!("以前");
        fixture.Session.BeforeReset = () => fixture.Receive!("停止中の通知");
        fixture.Session.AfterReset = () =>
        {
            Assert.Equal(string.Empty, owner.TakeLatest());
            fixture.Receive!("新しい結果");
        };
        if (words)
        {
            await owner.StartWordsAsync(["登録語"]);
            Assert.Equal(["登録語"], fixture.Session.Words);
        }
        else
        {
            await owner.StartGrammarFileAsync("words.xml");
            Assert.Equal("words.xml", fixture.Session.Path);
        }
        Assert.Equal("新しい結果", owner.TakeLatest());
        Assert.Equal(string.Empty, owner.TakeLatest());
        Assert.Equal(1, fixture.Created);
    }

    [Fact]
    public async Task StopRetainsLatestAndPendingWaitCanReceiveAfterRestart()
    {
        var fixture = new Fixture();
        await using var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        await owner.StartDictationAsync();
        fixture.Receive!("停止前");
        await owner.StopAsync();
        Assert.Equal("停止前", owner.TakeLatest());
        Task<string> waiting = owner.WaitForNextAsync();
        await owner.StopAsync();
        Assert.False(waiting.IsCompleted);
        await owner.StartDictationAsync();
        fixture.Receive!("再開後");
        Assert.Equal("再開後", await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("再開後", owner.TakeLatest());
    }

    [Fact]
    public async Task CancellingOneWaitDoesNotStopRecognitionOrOtherWait()
    {
        var fixture = new Fixture();
        await using var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        await owner.StartDictationAsync();
        using var cancellation = new CancellationTokenSource();
        Task<string> first = owner.WaitForNextAsync(cancellation.Token);
        Task<string> second = owner.WaitForNextAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        fixture.Receive!("継続");
        Assert.Equal("継続", await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, fixture.Session.Stops);
    }

    [Fact]
    public async Task NotificationFailureUnblocksWaitAndIsPreservedDuringCleanup()
    {
        var fixture = new Fixture();
        var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        await owner.StartDictationAsync();
        Task<string> waiting = owner.WaitForNextAsync();
        var failure = new InvalidDataException("通知の文字列取得に失敗");
        fixture.Session.CallbackFailure = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(() =>
            waiting.WaitAsync(TimeSpan.FromSeconds(10))));
        fixture.Receive!("失敗後");
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => owner.TakeLatest()));
        AggregateException cleanup = await Assert.ThrowsAsync<AggregateException>(() =>
            owner.DisposeAsync().AsTask());
        Assert.Contains(failure, cleanup.Flatten().InnerExceptions);
        Assert.Equal(1, fixture.Session.Disposals);
    }

    [Fact]
    public async Task DisposeUnblocksWaitBeforeComCleanupAndIgnoresLateNotifications()
    {
        var fixture = new Fixture();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Session.Cleanup = () => cleanup.Task;
        var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        await owner.StartDictationAsync();
        Task<string> waiting = owner.WaitForNextAsync();
        Task disposing = owner.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(disposing.IsCompleted);
            fixture.Receive!("終了後");
            Assert.Throws<ObjectDisposedException>(() => owner.TakeLatest());
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                owner.StartDictationAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Same(disposing, owner.DisposeAsync().AsTask());
        }
        finally
        {
            cleanup.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(1, fixture.Session.Disposals);
    }

    [Fact]
    public async Task DisposeWaitsForCreationAndReleasesReturnedSession()
    {
        var fixture = new Fixture();
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Creating = async () =>
        {
            entered.TrySetResult();
            await created.Task;
        };
        var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        Task starting = owner.StartDictationAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task disposing = owner.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        created.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, fixture.Session.Disposals);
        Assert.Equal(0, fixture.Session.Starts);
    }

    [Fact]
    public async Task CreationFailureIsNotRetriedAndUnblocksWaiting()
    {
        var fixture = new Fixture();
        var failure = new InvalidOperationException("作成失敗");
        fixture.Creating = () => throw failure;
        var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        Task<string> waiting = owner.WaitForNextAsync();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.StartDictationAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => waiting));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.StartDictationAsync());
        Assert.Equal(1, fixture.Created);
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task CancellingStartDuringCreationKeepsOwnedSessionForNextRequest()
    {
        var fixture = new Fixture();
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Creating = async () =>
        {
            entered.TrySetResult();
            await created.Task;
        };
        await using var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        using var cancellation = new CancellationTokenSource();
        Task starting = owner.StartDictationAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.False(starting.IsCompleted);
        created.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        await owner.StartDictationAsync();
        Assert.Equal(1, fixture.Created);
        Assert.Equal(1, fixture.Session.Starts);
    }

    [Fact]
    public async Task CleanupFailureAndFinalNotificationFailureAreBothReported()
    {
        var fixture = new Fixture();
        var cleanupFailure = new InvalidOperationException("解除失敗");
        var lateFailure = new InvalidDataException("終了中の通知失敗");
        fixture.Session.Cleanup = () =>
        {
            fixture.Session.CallbackFailure = lateFailure;
            throw cleanupFailure;
        };
        var owner = new SpeechRecognitionOwner(fixture.CreateAsync);
        await owner.StartDictationAsync();
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            owner.DisposeAsync().AsTask());
        Assert.Contains(cleanupFailure, failure.InnerExceptions);
        Assert.Contains(lateFailure, failure.InnerExceptions);
    }

    private sealed class Fixture
    {
        internal readonly Session Session = new();
        internal Action<string>? Receive;
        internal Func<Task>? Creating;
        internal int Created;

        internal async Task<ISpeechRecognitionSession> CreateAsync(
            Action<string> receive, CancellationToken token)
        {
            Created++;
            Receive = receive;
            if (Creating is not null)
            {
                await Creating();
            }

            return Session;
        }
    }

    private sealed class Session : ISpeechRecognitionSession
    {
        internal Action? BeforeReset;
        internal Action? AfterReset;
        internal Func<Task>? Cleanup;
        internal Exception? CallbackFailure;
        internal string? Path;
        internal IEnumerable<string>? Words;
        internal int Starts;
        internal int Stops;
        internal int Disposals;

        public Task StartDictationAsync(CancellationToken token = default,
            Action? beforeStart = null)
        {
            token.ThrowIfCancellationRequested();
            BeforeReset?.Invoke();
            beforeStart?.Invoke();
            Starts++;
            AfterReset?.Invoke();
            return Task.CompletedTask;
        }

        public Task StartGrammarFileAsync(string path, CancellationToken token = default,
            Action? beforeStart = null)
        {
            Path = path;
            return StartDictationAsync(token, beforeStart);
        }

        public Task StartWordsAsync(IEnumerable<string> words,
            CancellationToken token = default, Action? beforeStart = null)
        {
            Words = words;
            return StartDictationAsync(token, beforeStart);
        }

        public async Task StartEvaluatedWordsAsync(
            Func<CancellationToken, Task<IEnumerable<string>>> evaluate,
            CancellationToken token = default, Action? beforeStart = null)
        {
            await StartDictationAsync(token, beforeStart);
            Words = await evaluate(token);
        }

        public Task StopAsync(CancellationToken token = default)
        {
            Stops++;
            return Task.CompletedTask;
        }

        public Exception? TakeCallbackFailure() =>
            Interlocked.Exchange(ref CallbackFailure, null);

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return new(Cleanup?.Invoke() ?? Task.CompletedTask);
        }
    }
}
