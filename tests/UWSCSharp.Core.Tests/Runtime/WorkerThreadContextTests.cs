using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

// 所有する通常スレッドだけを使い、画面・入力・COMを操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WorkerThreadContextTests
{
    [Fact]
    public async Task RunsInitializationContinuationsAndFinishOnOwnerThread()
    {
        List<int> threads = [];
        int finishes = 0;
        var worker = new WorkerThreadContext(async token =>
        {
            Assert.NotNull(SynchronizationContext.Current);
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Delay(1, token);
            threads.Add(Environment.CurrentManagedThreadId);
        }, initialize: () => threads.Add(Environment.CurrentManagedThreadId),
            finish: () =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                finishes++;
            });
        await WaitAsync(worker.Ready);
        await WaitAsync(worker.Completion);
        Assert.Equal(5, threads.Count);
        Assert.All(threads, thread =>
            Assert.Equal(worker.ManagedThreadId, thread));
        Assert.False(worker.IsAlive);
        await WaitAsync(worker.DisposeAsync().AsTask());
        await WaitAsync(worker.DisposeAsync().AsTask());
        Assert.Equal(1, finishes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupFailureNeverRunsInitializationOrRoot(
        bool startsBeforeFailure)
    {
        var expected = new InvalidOperationException("起動の失敗");
        int initialization = 0;
        int roots = 0;
        int finishes = 0;
        var worker = new WorkerThreadContext(_ =>
        {
            roots++;
            return Task.CompletedTask;
        }, initialize: () => initialization++, finish: () => finishes++,
            startThread: thread =>
            {
                if (startsBeforeFailure)
                {
                    thread.Start();
                }

                throw expected;
            });
        Assert.Same(expected,
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitAsync(worker.Ready)));
        Assert.Same(expected,
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitAsync(worker.Completion)));
        Assert.False(worker.IsAlive);
        Assert.Equal(0, initialization);
        Assert.Equal(0, roots);
        Assert.Equal(0, finishes);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(worker.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task InitializationFailureStillFinishesOnOwnerThread()
    {
        var expected = new InvalidOperationException("初期化の失敗");
        int initializationThread = 0;
        int finishThread = 0;
        int roots = 0;
        var worker = new WorkerThreadContext(_ =>
        {
            roots++;
            return Task.CompletedTask;
        }, initialize: () =>
        {
            initializationThread = Environment.CurrentManagedThreadId;
            throw expected;
        }, finish: () => finishThread = Environment.CurrentManagedThreadId);
        Assert.Same(expected,
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitAsync(worker.Ready)));
        Assert.Same(expected,
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitAsync(worker.Completion)));
        Assert.Equal(worker.ManagedThreadId, initializationThread);
        Assert.Equal(worker.ManagedThreadId, finishThread);
        Assert.Equal(0, roots);
        Assert.False(worker.IsAlive);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(worker.DisposeAsync().AsTask()));
    }

    [Fact]
    public async Task PreservesRootExceptionAndCleanupFailure()
    {
        var primary = new InvalidOperationException("ルートの失敗");
        var cleanup = new IOException("終了処理の失敗");
        var worker = new WorkerThreadContext(async _ =>
        {
            await Task.Yield();
            throw primary;
        }, finish: () => throw cleanup);
        await WaitAsync(worker.Ready);
        Assert.Same(primary,
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitAsync(worker.Completion)));
        AggregateException errors = Assert.IsType<AggregateException>(
            primary.Data[WorkerThreadContext.CleanupErrorsKey]);
        Assert.Same(cleanup, Assert.Single(errors.InnerExceptions));
        Assert.False(worker.IsAlive);
        Assert.Same(primary,
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitAsync(worker.DisposeAsync().AsTask())));
    }

    [Fact]
    public async Task PropagatesCooperativeCancellationAndJoinsOwner()
    {
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource entered = Signal();
        CancellationToken supplied = default;
        int finishes = 0;
        var worker = new WorkerThreadContext(async token =>
        {
            supplied = token;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancellation.Token, finish: () => finishes++);
        try
        {
            await WaitAsync(entered.Task);
            cancellation.Cancel();
            OperationCanceledException error =
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => WaitAsync(worker.Completion));
            Assert.Equal(supplied, error.CancellationToken);
            Assert.True(worker.Completion.IsCanceled);
            Assert.False(worker.IsAlive);
            Assert.Equal(1, finishes);
        }
        finally
        {
            await WaitAsync(worker.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DisposeWaitsForRootThatDoesNotObserveCancellation()
    {
        TaskCompletionSource entered = Signal();
        TaskCompletionSource stopObserved = Signal();
        TaskCompletionSource release = Signal();
        int finishes = 0;
        var worker = new WorkerThreadContext(async token =>
        {
            using CancellationTokenRegistration registration = token.Register(
                () => stopObserved.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        }, finish: () => finishes++);
        try
        {
            await WaitAsync(entered.Task);
            Task disposing = worker.DisposeAsync().AsTask();
            await WaitAsync(stopObserved.Task);
            Assert.False(disposing.IsCompleted);
            Assert.True(worker.IsAlive);
            release.TrySetResult();
            await WaitAsync(disposing);
            Assert.False(worker.IsAlive);
            Assert.True(worker.Completion.IsCompletedSuccessfully);
            Assert.Equal(1, finishes);
        }
        finally
        {
            release.TrySetResult();
            await WaitAsync(worker.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DoesNotMoveUncapturedContinuationBackToOwner()
    {
        TaskCompletionSource waiting = Signal();
        TaskCompletionSource release = Signal();
        int before = 0;
        int after = 0;
        var worker = new WorkerThreadContext(async token =>
        {
            before = Environment.CurrentManagedThreadId;
            SynchronizationContext.Current!.Post(
                _ => waiting.TrySetResult(), null);
            await release.Task.ConfigureAwait(false);
            after = Environment.CurrentManagedThreadId;
        });
        try
        {
            // この通知はルート処理が待機してキューへ戻った後に実行されます。
            await WaitAsync(waiting.Task);
            release.TrySetResult();
            await WaitAsync(worker.Completion);
            Assert.Equal(worker.ManagedThreadId, before);
            Assert.NotEqual(before, after);
            Assert.False(worker.IsAlive);
        }
        finally
        {
            release.TrySetResult();
            await WaitAsync(worker.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task SendsToOwnerAndReturnsCallbackFailureToSender()
    {
        TaskCompletionSource waiting = Signal();
        TaskCompletionSource release = Signal();
        SynchronizationContext? captured = null;
        int callbackThread = 0;
        var expected = new InvalidOperationException("Sendの失敗");
        var worker = new WorkerThreadContext(async token =>
        {
            captured = SynchronizationContext.Current;
            captured!.Post(_ => waiting.TrySetResult(), null);
            await release.Task;
        });
        try
        {
            await WaitAsync(waiting.Task);
            InvalidOperationException error =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    WaitAsync(Task.Run(() => captured!.Send(_ =>
                    {
                        callbackThread = Environment.CurrentManagedThreadId;
                        throw expected;
                    }, null))));
            Assert.Same(expected, error);
            Assert.Equal(worker.ManagedThreadId, callbackThread);
            release.TrySetResult();
            await WaitAsync(worker.Completion);
        }
        finally
        {
            release.TrySetResult();
            await WaitAsync(worker.DisposeAsync().AsTask());
        }
    }

    private static TaskCompletionSource Signal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WaitAsync(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(10));
}
