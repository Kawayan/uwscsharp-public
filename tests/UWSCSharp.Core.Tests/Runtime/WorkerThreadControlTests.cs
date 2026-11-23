using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

// 所有Threadとfake優先度だけを使い、画面・入力・OS優先度を変更しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WorkerThreadControlTests
{
    [Fact]
    public async Task SuspendAndResumeWaitAtCheckpointOnOwnerThread()
    {
        TaskCompletionSource before = NewSignal();
        TaskCompletionSource enter = NewSignal();
        TaskCompletionSource passed = NewSignal();
        var priority = new FakePriority();
        var control = new WorkerThreadControl(priority);
        List<int> threads = [];
        control.Start(async token =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            before.SetResult();
            await enter.Task;
            await control.CheckpointAsync(token);
            threads.Add(Environment.CurrentManagedThreadId);
            passed.SetResult();
        });
        try
        {
            await WaitAsync(before.Task);
            Assert.True(control.Apply((int)WorkerThreadCommand.Suspend));
            enter.SetResult();
            Assert.True(control.IsSuspended);
            Assert.False(passed.Task.IsCompleted);
            Assert.True(control.Apply((int)WorkerThreadCommand.Resume));
            await WaitAsync(passed.Task);
            await WaitAsync(control.Completion);
            Assert.All(threads, id => Assert.Equal(control.ManagedThreadId, id));
            Assert.Equal(control.ManagedThreadId, priority.InitializedOn);
            Assert.Equal(control.ManagedThreadId, priority.DisposedOn);
            Assert.Equal(1, priority.Disposals);
        }
        finally
        {
            enter.TrySetResult();
            await WaitAsync(control.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootCancellationAndQuitReleaseSuspendedCheckpoint(bool quit)
    {
        using var cancellation = new CancellationTokenSource();
        TaskCompletionSource enter = NewSignal();
        TaskCompletionSource entered = NewSignal();
        var control = new WorkerThreadControl();
        control.Start(async _ =>
        {
            await enter.Task;
            entered.SetResult();
            // root tokenを明示しなくてもQuitと親取消は解除します。
            await control.CheckpointAsync(CancellationToken.None);
        }, cancellation.Token);
        try
        {
            await WaitAsync(control.Ready);
            control.Apply((int)WorkerThreadCommand.Suspend);
            enter.SetResult();
            await WaitAsync(entered.Task);
            if (quit)
            {
                control.Apply((int)WorkerThreadCommand.Quit);
            }
            else
            {
                cancellation.Cancel();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WaitAsync(control.Completion));
            Assert.False(control.IsSuspended);
        }
        finally
        {
            enter.TrySetResult();
            await WaitAsync(control.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task QuitPublishesCancellationBeforeResumingCheckpoint()
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            TaskCompletionSource enter = NewSignal();
            TaskCompletionSource entered = NewSignal();
            var control = new WorkerThreadControl();
            control.Start(async _ =>
            {
                await enter.Task;
                entered.SetResult();
                await control.CheckpointAsync(CancellationToken.None);
            });
            try
            {
                await WaitAsync(control.Ready);
                control.Apply((int)WorkerThreadCommand.Suspend);
                enter.SetResult();
                await WaitAsync(entered.Task);
                control.Apply((int)WorkerThreadCommand.Quit);
                OperationCanceledException error =
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(
                        () => WaitAsync(control.Completion));
                Assert.True(error.CancellationToken.IsCancellationRequested);
            }
            finally
            {
                enter.TrySetResult();
                await WaitAsync(control.DisposeAsync().AsTask());
            }
        }
    }

    [Fact]
    public async Task AllSixPrioritiesTargetInitializedOwnerAndStopAfterFinish()
    {
        TaskCompletionSource finish = NewSignal();
        var priority = new FakePriority();
        var control = new WorkerThreadControl(priority);
        control.Start(_ => finish.Task);
        try
        {
            await WaitAsync(control.Ready);
            for (int mode = 10; mode <= 15; mode++)
            {
                Assert.True(control.Apply(mode));
            }

            Assert.Equal(new[] { -15, -2, -1, 0, 1, 2 }, priority.Values);
            Assert.Equal(control.ManagedThreadId, priority.InitializedOn);
            Assert.False(control.Apply(0));
            Assert.False(control.Apply(4));
            Assert.False(control.Apply(9));
            Assert.False(control.Apply(16));
            Assert.False(control.Apply(int.MaxValue));
            finish.SetResult();
            await WaitAsync(control.Completion);
            Assert.False(control.Apply((int)WorkerThreadCommand.Highest));
            Assert.False(control.Apply((int)WorkerThreadCommand.Suspend));
            Assert.Equal(6, priority.Values.Count);
            Assert.Equal(1, priority.Disposals);
        }
        finally
        {
            finish.TrySetResult();
            await WaitAsync(control.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task PriorityFailureIsReturnedAndPreservedThroughJoin()
    {
        var expected = new InvalidOperationException("fake優先度失敗");
        var priority = new FakePriority { SetError = expected };
        var control = new WorkerThreadControl(priority);
        control.Start(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        try
        {
            await WaitAsync(control.Ready);
            Assert.Same(expected, Assert.Throws<InvalidOperationException>(
                () => control.Apply((int)WorkerThreadCommand.Higher)));
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitAsync(control.Completion)));
            Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitAsync(control.DisposeAsync().AsTask())));
            Assert.Equal(1, priority.Disposals);
            Assert.Equal(control.ManagedThreadId, priority.DisposedOn);
        }
        finally
        {
            try
            {
                await WaitAsync(control.DisposeAsync().AsTask());
            }
            catch (InvalidOperationException error) when (ReferenceEquals(error, expected))
            {
                // 検証対象の制御失敗はCompletionに保持されています。
            }
        }
    }

    [Fact]
    public async Task InitializationFailureNeverRunsRootAndDisposesBackend()
    {
        var expected = new InvalidOperationException("fake初期化失敗");
        var priority = new FakePriority { InitializeError = expected };
        var control = new WorkerThreadControl(priority);
        bool ran = false;
        control.Start(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(control.Ready)));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(control.Completion)));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(control.DisposeAsync().AsTask())));
        Assert.False(ran);
        Assert.Equal(1, priority.Disposals);
    }

    [Fact]
    public async Task RootFailureKeepsBackendCleanupFailureAsSecondary()
    {
        var expected = new InvalidOperationException("root失敗");
        var cleanup = new IOException("backend解放失敗");
        var priority = new FakePriority { DisposeError = cleanup };
        var control = new WorkerThreadControl(priority);
        control.Start(_ => Task.FromException(expected));
        await WaitAsync(control.Ready);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(control.Completion)));
        AggregateException secondary = Assert.IsType<AggregateException>(
            expected.Data[WorkerThreadContext.CleanupErrorsKey]);
        Assert.Contains(cleanup, secondary.InnerExceptions);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(
            () => WaitAsync(control.DisposeAsync().AsTask())));
        Assert.Equal(1, priority.Disposals);
    }

    [Fact]
    public async Task DisposeWaitsForNoncooperativeRootAndDisposesExactlyOnce()
    {
        TaskCompletionSource release = NewSignal();
        var priority = new FakePriority();
        var control = new WorkerThreadControl(priority);
        control.Start(_ => release.Task);
        try
        {
            await WaitAsync(control.Ready);
            Task disposing = control.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            release.SetResult();
            await WaitAsync(disposing);
            await WaitAsync(control.Completion);
            await WaitAsync(control.DisposeAsync().AsTask());
            Assert.Equal(1, priority.Disposals);
            Assert.Equal(control.ManagedThreadId, priority.DisposedOn);
        }
        finally
        {
            release.TrySetResult();
            await WaitAsync(control.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DisposeBeforeStartRejectsNewOwnership()
    {
        var priority = new FakePriority();
        var control = new WorkerThreadControl(priority);
        Assert.False(control.Apply((int)WorkerThreadCommand.Normal));
        await control.DisposeAsync();
        await control.DisposeAsync();
        Assert.Throws<InvalidOperationException>(
            () => control.Start(_ => Task.CompletedTask));
        Assert.Equal(1, priority.Disposals);
    }

    [Fact]
    public async Task RegistersNineOriginalThreadConstants()
    {
        List<string> values = [];
        await new Interpreter().RunAsync(
            "PRINT TRD_SUSPEND\nPRINT TRD_QUIT\nPRINT TRD_RESUME\n"
            + "PRINT TRD_IDLE\nPRINT TRD_LOWEST\nPRINT TRD_LOWER\n"
            + "PRINT TRD_NORMAL\nPRINT TRD_HIGHER\nPRINT TRD_HIGHEST",
            values.Add);
        Assert.Equal(new[] { "1", "2", "3", "10", "11", "12", "13", "14", "15" },
            values);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WaitAsync(Task task)
        => task.WaitAsync(TimeSpan.FromSeconds(8));

    private sealed class FakePriority : IWorkerThreadPriority
    {
        internal Exception? InitializeError
        {
            get; init;
        }

        internal Exception? SetError
        {
            get; init;
        }

        internal Exception? DisposeError
        {
            get; init;
        }

        internal int InitializedOn
        {
            get; private set;
        }

        internal int DisposedOn
        {
            get; private set;
        }

        internal int Disposals
        {
            get; private set;
        }

        internal List<int> Values { get; } = [];

        public void Initialize()
        {
            InitializedOn = Environment.CurrentManagedThreadId;
            if (InitializeError is not null)
            {
                throw InitializeError;
            }
        }

        public void SetPriority(WorkerThreadPriority priority)
        {
            Assert.Equal(0, Disposals);
            Values.Add((int)priority);
            if (SetError is not null)
            {
                throw SetError;
            }
        }

        public void Dispose()
        {
            DisposedOn = Environment.CurrentManagedThreadId;
            Disposals++;
            if (DisposeError is not null)
            {
                throw DisposeError;
            }
        }
    }
}
