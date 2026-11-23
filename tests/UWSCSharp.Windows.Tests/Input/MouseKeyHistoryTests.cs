using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class MouseKeyHistoryTests
{
    [Theory]
    [InlineData((short)1)]
    [InlineData(short.MinValue)]
    [InlineData((short)-32767)]
    public void PollUsesWholeAsyncWordAndOnlyPhysicalMouseKeys(short state)
    {
        List<int> reads = [];
        var history = new MouseKeyHistory();
        var backend = new Backend(key =>
        {
            reads.Add(key);
            return state;
        });

        history.Poll(backend, default);

        Assert.Equal([1, 2, 4], reads);
        Assert.True(history.Consume(1));
        Assert.True(history.Consume(2));
        Assert.True(history.Consume(4));
        Assert.False(history.Consume(1));
        Assert.False(history.Consume(2));
        Assert.False(history.Consume(4));
    }

    [Fact]
    public void ReleaseKeepsOnePendingObservationUntilConsumed()
    {
        short state = short.MinValue;
        var history = new MouseKeyHistory();
        var backend = new Backend(key => key == 1 ? state : (short)0);

        history.Poll(backend, default);
        state = 0;
        history.Poll(backend, default);

        Assert.True(history.Consume(1));
        Assert.False(history.Consume(1));
    }

    [Fact]
    public void HeldAndRepeatedObservationsCoalesceWithoutClearingOtherKeys()
    {
        var history = new MouseKeyHistory();
        var backend = new Backend(_ => short.MinValue);

        history.Poll(backend, default);
        history.Poll(backend, default);
        Assert.True(history.Consume(1));
        Assert.False(history.Consume(1));
        Assert.False(history.Consume(3));
        Assert.False(history.Consume(0));
        Assert.False(history.Consume(-1));
        Assert.True(history.Consume(2));
        Assert.True(history.Consume(4));

        history.Poll(backend, default);
        Assert.True(history.Consume(1));
    }

    [Fact]
    public void ClearAndSeparateInstancesKeepRunsIndependent()
    {
        var first = new MouseKeyHistory();
        var second = new MouseKeyHistory();
        first.Poll(new Backend(_ => 1), default);

        Assert.False(second.Consume(1));
        Assert.False(second.Consume(2));
        Assert.False(second.Consume(4));
        first.Clear();
        Assert.False(first.Consume(1));
        Assert.False(first.Consume(2));
        Assert.False(first.Consume(4));
        first.Poll(new Backend(key => key == 4 ? (short)1 : (short)0), default);
        Assert.True(first.Consume(4));
    }

    [Fact]
    public void PrecancelDoesNotReadOrConsumeExistingHistory()
    {
        var history = new MouseKeyHistory();
        history.Poll(new Backend(key => key == 2 ? (short)1 : (short)0), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var backend = new Backend(_ =>
            throw new InvalidOperationException("取消後の取得は禁止です。"));

        OperationCanceledException error = Assert.ThrowsAny<OperationCanceledException>(
            () => history.Poll(backend, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(history.Consume(2));
    }

    [Fact]
    public void CancellationDuringPollDoesNotPublishPartialObservation()
    {
        var history = new MouseKeyHistory();
        List<int> reads = [];
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend(key =>
        {
            reads.Add(key);
            if (key == 2)
            {
                cancellation.Cancel();
            }

            return 1;
        });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            history.Poll(backend, cancellation.Token));

        Assert.Equal([1, 2], reads);
        Assert.False(history.Consume(1));
        Assert.False(history.Consume(2));
        Assert.False(history.Consume(4));
    }

    [Fact]
    public async Task ConcurrentPollsPreserveEveryPhysicalKey()
    {
        var history = new MouseKeyHistory();
        using var gate = new Barrier(3);
        var workers = new Task[3];
        for (int index = 0; index < workers.Length; index++)
        {
            int physicalKey = index switch
            {
                0 => 1,
                1 => 2,
                _ => 4
            };
            bool checksResult = index == 0;
            workers[index] = Task.Factory.StartNew(() =>
            {
                var backend = new Backend(key =>
                    key == physicalKey ? (short)1 : (short)0);
                for (int round = 0; round < 32; round++)
                {
                    Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
                    history.Poll(backend, default);
                    Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
                    if (checksResult)
                    {
                        Assert.True(history.Consume(1));
                        Assert.True(history.Consume(2));
                        Assert.True(history.Consume(4));
                        Assert.False(history.Consume(1));
                    }

                    Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        await Task.WhenAll(workers);
    }

    [Fact]
    public void ConcurrentConsumersReturnOneObservationExactlyOnce()
    {
        var history = new MouseKeyHistory();
        history.Poll(new Backend(key => key == 1 ? (short)1 : (short)0), default);
        int consumed = 0;

        Parallel.For(0, 128, _ =>
        {
            if (history.Consume(1))
            {
                Interlocked.Increment(ref consumed);
            }
        });

        Assert.Equal(1, consumed);
        Assert.False(history.Consume(1));
    }

    [Fact]
    public async Task PollAndConsumePreserveTheOtherPhysicalKey()
    {
        var history = new MouseKeyHistory();
        using var gate = new Barrier(2);
        Task polling = Task.Factory.StartNew(() =>
        {
            var first = new Backend(key => key == 1 ? (short)1 : (short)0);
            var second = new Backend(key =>
            {
                if (key == 4)
                {
                    Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
                }

                return key == 2 ? (short)1 : (short)0;
            });
            for (int round = 0; round < 256; round++)
            {
                history.Poll(first, default);
                history.Poll(second, default);
                Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
                Assert.False(history.Consume(1));
                Assert.True(history.Consume(2));
                Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Task consuming = Task.Factory.StartNew(() =>
        {
            for (int round = 0; round < 256; round++)
            {
                Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
                Assert.True(history.Consume(1));
                Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
                Assert.True(gate.SignalAndWait(TimeSpan.FromSeconds(10)));
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await Task.WhenAll(polling, consuming);
    }

    private sealed class Backend(Func<int, short> read) : IKeyStateBackend
    {
        public short ReadAsync(int key) => read(key);

        public short ReadToggle(int key) => throw new InvalidOperationException(
            "マウス採取でトグル状態を取得してはいけません。");

        public bool ReadIme(CancellationToken token) =>
            throw new InvalidOperationException(
                "マウス採取でIME状態を取得してはいけません。");
    }
}
