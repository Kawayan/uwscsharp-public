using Xunit;

namespace UWSCSharp.Windows.Tests;

// 文字列とTaskだけを使い、認識器・COM・音声入力を起動しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionResultsTests
{
    [Fact]
    public async Task FailureWakesAllWaitersAndPreservesFirstError()
    {
        using var results = new SpeechRecognitionResults();
        Task<string> first = results.WaitForNextAsync();
        Task<string> second = results.WaitForNextAsync();
        var failure = new InvalidDataException("認識通知失敗");
        results.Fail(failure);
        results.Fail(new InvalidOperationException("後の失敗"));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(() => first));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(() => second));
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => results.TakeLatest()));
        Assert.Same(failure, Assert.Throws<InvalidDataException>(() => results.Publish("後の通知")));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(() =>
            results.WaitForNextAsync()));
    }

    [Fact]
    public void FailureAfterDisposalDoesNotRestoreAvailability()
    {
        using var results = new SpeechRecognitionResults();
        results.Dispose();
        results.Fail(new InvalidDataException("終了後"));
        Assert.Throws<ObjectDisposedException>(() => results.TakeLatest());
    }

    [Fact]
    public void NonWaitingReadReturnsOnlyLatestThenClearsIt()
    {
        using var results = new SpeechRecognitionResults();
        Assert.Equal(string.Empty, results.TakeLatest());
        results.Publish("古い結果");
        results.Publish("最新😀\0結果");
        Assert.Equal("最新😀\0結果", results.TakeLatest());
        Assert.Equal(string.Empty, results.TakeLatest());
    }

    [Fact]
    public async Task WaitingDiscardsOldResultAndKeepsNewResultAfterReturn()
    {
        using var results = new SpeechRecognitionResults();
        results.Publish("待機前");
        Task<string> waiting = results.WaitForNextAsync();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(string.Empty, results.TakeLatest());
        results.Publish("次の結果");
        Assert.Equal("次の結果", await Bounded(waiting));
        Assert.Equal("次の結果", results.TakeLatest());
        Assert.Equal(string.Empty, results.TakeLatest());
    }

    [Fact]
    public async Task RepeatedWaitingRequiresAnotherPublication()
    {
        using var results = new SpeechRecognitionResults();
        Task<string> first = results.WaitForNextAsync();
        results.Publish("同じ語");
        Assert.Equal("同じ語", await Bounded(first));
        Task<string> second = results.WaitForNextAsync();
        Assert.False(second.IsCompleted);
        results.Publish("同じ語");
        Assert.Equal("同じ語", await Bounded(second));
    }

    [Fact]
    public async Task EmptyPublicationClearsLatestWithoutCompletingWait()
    {
        using var results = new SpeechRecognitionResults();
        results.Publish("消す結果");
        results.Publish(string.Empty);
        Assert.Equal(string.Empty, results.TakeLatest());
        Task<string> waiting = results.WaitForNextAsync();
        results.Publish(string.Empty);
        Assert.False(waiting.IsCompleted);
        results.Publish(" ");
        Assert.Equal(" ", await Bounded(waiting));
    }

    [Fact]
    public async Task CallerCancellationDoesNotCloseStoreOrOtherWaiters()
    {
        using var results = new SpeechRecognitionResults();
        using var cancellation = new CancellationTokenSource();
        Task<string> cancelled = results.WaitForNextAsync(cancellation.Token);
        Task<string> remaining = results.WaitForNextAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Bounded(cancelled));
        Assert.False(remaining.IsCompleted);
        results.Publish("取消後");
        Assert.Equal("取消後", await Bounded(remaining));
        Assert.Equal("取消後", results.TakeLatest());
    }

    [Fact]
    public void AlreadyCancelledWaitDoesNotEraseResult()
    {
        using var results = new SpeechRecognitionResults();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        results.Publish("保持");
        Assert.ThrowsAny<OperationCanceledException>(
            () =>
            {
                _ = results.WaitForNextAsync(cancellation.Token);
            });
        Assert.Equal("保持", results.TakeLatest());
    }

    [Fact]
    public async Task WaitersObserveSharedResultWithoutConsumingIt()
    {
        using var results = new SpeechRecognitionResults();
        Task<string> first = results.WaitForNextAsync();
        Task<string> second = results.WaitForNextAsync();
        results.Publish("共有");
        Assert.Equal(["共有", "共有"],
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("共有", results.TakeLatest());
    }

    [Fact]
    public async Task DisposalReleasesWaitersAndRejectsLateNotifications()
    {
        var results = new SpeechRecognitionResults();
        Task<string> first = results.WaitForNextAsync();
        Task<string> second = results.WaitForNextAsync();
        results.Dispose();
        results.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Bounded(first));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Bounded(second));
        Assert.Throws<ObjectDisposedException>(() => results.Publish("遅延通知"));
        Assert.Throws<ObjectDisposedException>(() => results.TakeLatest());
        Assert.Throws<ObjectDisposedException>(
            () =>
            {
                _ = results.WaitForNextAsync();
            });
        using var next = new SpeechRecognitionResults();
        next.Publish("別の認識");
        Assert.Equal("別の認識", next.TakeLatest());
    }

    [Fact]
    public void NullPublicationDoesNotChangePendingResult()
    {
        using var results = new SpeechRecognitionResults();
        results.Publish("保持");
        Assert.Throws<ArgumentNullException>(() => results.Publish(null!));
        Assert.Equal("保持", results.TakeLatest());
    }

    private static Task<string> Bounded(Task<string> task) =>
        task.WaitAsync(TimeSpan.FromSeconds(10));
}
