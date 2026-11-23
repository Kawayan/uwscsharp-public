using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ShortcutKeySequenceTests
{
    [Fact]
    public async Task ClicksOrdinaryKeysAndReleasesModifiersInReverseOrder()
    {
        var backend = new Backend();

        await ShortcutKeySequence.RunAsync([17, 16, 65, 66], backend, default);

        Assert.Equal([(17, 1), (16, 1), (65, 0), (66, 0), (16, 2), (17, 2)],
            backend.Events);
        Assert.Empty(backend.Held);
        Assert.Equal(4, backend.Delays);
        Assert.All(backend.Sends.Where(send => send.Mode == 2),
            send => Assert.Equal(CancellationToken.None, send.Token));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(160)]
    [InlineData(161)]
    [InlineData(162)]
    [InlineData(163)]
    [InlineData(164)]
    [InlineData(165)]
    [InlineData(91)]
    [InlineData(92)]
    public async Task HoldsEverySupportedModifier(int modifier)
    {
        var backend = new Backend();

        await ShortcutKeySequence.RunAsync([modifier, 65], backend, default);

        Assert.Equal([(modifier, 1), (65, 0), (modifier, 2)], backend.Events);
    }

    [Fact]
    public async Task DoesNotPressOrReleaseDuplicateModifiersTwice()
    {
        var backend = new Backend();

        await ShortcutKeySequence.RunAsync([17, 17, 65, 17], backend, default);

        Assert.Equal([(17, 1), (65, 0), (17, 2)], backend.Events);
        Assert.Equal(2, backend.Delays);
    }

    [Fact]
    public async Task PreservesPreviouslyHeldModifiersAndOrdinaryKeys()
    {
        var backend = new Backend();
        backend.Held.UnionWith([17, 65]);

        await ShortcutKeySequence.RunAsync([17, 65, 16, 66], backend, default);

        Assert.Equal([(65, 1), (16, 1), (66, 0), (16, 2)], backend.Events);
        Assert.True(backend.IsHeld(17));
        Assert.True(backend.IsHeld(65));
        Assert.False(backend.IsHeld(16));
        Assert.False(backend.IsHeld(66));
    }

    [Fact]
    public async Task ReleasesClickLeftHeldByFailureAfterDown()
    {
        var failure = new InvalidOperationException("CLICK途中の障害");
        var backend = new Backend
        {
            AfterDown = (key, _) =>
            {
                if (key == 65)
                {
                    throw failure;
                }
            }
        };

        InvalidOperationException actual =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ShortcutKeySequence.RunAsync([17, 65], backend, default));

        Assert.Same(failure, actual);
        Assert.Equal([(17, 1), (65, 0), (65, 2), (17, 2)], backend.Events);
        Assert.Empty(backend.Held);
    }

    [Fact]
    public async Task ReleasesModifierWhenDownFailsAfterTracking()
    {
        var failure = new InvalidOperationException("DOWN後の障害");
        var backend = new Backend
        {
            AfterDown = (_, _) => throw failure
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShortcutKeySequence.RunAsync([17], backend, default));

        Assert.Equal([(17, 1), (17, 2)], backend.Events);
        Assert.Empty(backend.Held);
    }

    [Fact]
    public async Task ReleasesRepeatedClickFromItsLatestAttemptPosition()
    {
        int clicks = 0;
        var backend = new Backend
        {
            AfterDown = (key, _) =>
            {
                if (key == 65 && ++clicks == 2)
                {
                    throw new InvalidOperationException("2回目のCLICK障害");
                }
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShortcutKeySequence.RunAsync([65, 17, 65], backend, default));

        Assert.Equal([(65, 0), (17, 1), (65, 0), (65, 2), (17, 2)],
            backend.Events);
        Assert.Empty(backend.Held);
    }

    [Fact]
    public async Task PrecancelDoesNotSendOrDelay()
    {
        var backend = new Backend();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ShortcutKeySequence.RunAsync([17, 65], backend,
                    cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(backend.Events);
        Assert.Equal(0, backend.Delays);
    }

    [Fact]
    public async Task CancellationReleasesInputWithUncancelableToken()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new Backend
        {
            AfterDown = (_, _) => cancellation.Cancel()
        };

        OperationCanceledException error =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ShortcutKeySequence.RunAsync([17, 65], backend,
                    cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal([(17, 1), (17, 2)], backend.Events);
        Assert.Equal(CancellationToken.None, backend.Sends[^1].Token);
        Assert.Empty(backend.Held);
    }

    [Fact]
    public async Task DelayFailureStillReleasesEarlierModifiers()
    {
        var failure = new InvalidOperationException("待機の障害");
        var backend = new Backend
        {
            OnDelay = count =>
            {
                if (count == 2)
                {
                    throw failure;
                }
            }
        };

        InvalidOperationException actual =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ShortcutKeySequence.RunAsync([17, 65], backend, default));

        Assert.Same(failure, actual);
        Assert.Equal([(17, 1), (17, 2)], backend.Events);
        Assert.Empty(backend.Held);
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplaceCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new InvalidOperationException("取消後のUP障害");
        var backend = new Backend
        {
            AfterDown = (_, _) => cancellation.Cancel(),
            Before = (_, mode) =>
            {
                if (mode == 2)
                {
                    throw failure;
                }
            }
        };

        OperationCanceledException actual =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ShortcutKeySequence.RunAsync([17], backend,
                    cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        AggregateException cleanup = Assert.IsType<AggregateException>(
            actual.Data[ShortcutKeySequence.CleanupErrorsKey]);
        Assert.Equal([failure], cleanup.InnerExceptions);
        Assert.Equal(CancellationToken.None, backend.Sends[^1].Token);
    }

    [Fact]
    public async Task SingleCleanupFailurePreservesException()
    {
        var failure = new InvalidOperationException("UPの障害");
        var backend = new Backend
        {
            Before = (_, mode) =>
            {
                if (mode == 2)
                {
                    throw failure;
                }
            }
        };

        InvalidOperationException actual =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ShortcutKeySequence.RunAsync([17], backend, default));

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task MultipleCleanupFailuresDoNotSkipRemainingKeys()
    {
        var controlFailure = new InvalidOperationException("CTRLのUP障害");
        var shiftFailure = new InvalidOperationException("SHIFTのUP障害");
        var backend = new Backend
        {
            Before = (key, mode) =>
            {
                if (mode == 2)
                {
                    throw key == 17 ? controlFailure : shiftFailure;
                }
            }
        };

        AggregateException actual = await Assert.ThrowsAsync<AggregateException>(
            () => ShortcutKeySequence.RunAsync([17, 16, 65], backend, default));

        Assert.Equal([shiftFailure, controlFailure], actual.InnerExceptions);
        Assert.Equal([(17, 1), (16, 1), (65, 0), (16, 2), (17, 2)],
            backend.Events);
    }

    [Fact]
    public async Task CleanupFailuresAppendToPrimaryAndKeepTryingOtherKeys()
    {
        var primary = new InvalidOperationException("本処理の障害");
        var previous = new InvalidOperationException("既存の解放障害");
        var keyFailure = new InvalidOperationException("文字キーのUP障害");
        var shiftFailure = new InvalidOperationException("SHIFTのUP障害");
        primary.Data[ShortcutKeySequence.CleanupErrorsKey] =
            new AggregateException(previous);
        var backend = new Backend
        {
            AfterDown = (key, _) =>
            {
                if (key == 65)
                {
                    throw primary;
                }
            },
            Before = (key, mode) =>
            {
                if (mode == 2 && key is 65 or 16)
                {
                    throw key == 65 ? keyFailure : shiftFailure;
                }
            }
        };

        InvalidOperationException actual =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ShortcutKeySequence.RunAsync([17, 16, 65], backend, default));

        Assert.Same(primary, actual);
        AggregateException cleanup = Assert.IsType<AggregateException>(
            actual.Data[ShortcutKeySequence.CleanupErrorsKey]);
        Assert.Equal([previous, keyFailure, shiftFailure], cleanup.InnerExceptions);
        Assert.Equal([(17, 1), (16, 1), (65, 0), (65, 2), (16, 2), (17, 2)],
            backend.Events);
        Assert.False(backend.IsHeld(17));
    }

    private sealed class Backend : IShortcutKeyBackend
    {
        internal HashSet<int> Held
        {
            get;
        } = [];

        internal List<(int Key, int Mode, CancellationToken Token)> Sends
        {
            get;
        } = [];

        internal IEnumerable<(int Key, int Mode)> Events =>
            Sends.Select(send => (send.Key, send.Mode));

        internal int Delays;
        internal Action<int, int>? Before;
        internal Action<int, int>? AfterDown;
        internal Action<int>? OnDelay;

        public bool IsHeld(int rawKey) => Held.Contains(rawKey);

        public ValueTask SendAsync(int rawKey, int mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sends.Add((rawKey, mode, cancellationToken));
            Before?.Invoke(rawKey, mode);
            if (mode != 2)
            {
                Held.Add(rawKey);
                AfterDown?.Invoke(rawKey, mode);
                if (mode == 0)
                {
                    Held.Remove(rawKey);
                }
            }
            else
            {
                Held.Remove(rawKey);
            }

            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays++;
            OnDelay?.Invoke(Delays);
            return Task.CompletedTask;
        }
    }
}
