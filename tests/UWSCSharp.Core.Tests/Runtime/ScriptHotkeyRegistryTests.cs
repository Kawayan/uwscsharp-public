using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptHotkeyRegistryTests
{
    private static readonly ScriptHotkey Key = new(65, 0);

    [Fact]
    public async Task ReplacementInvalidatesQueuedAndLateOldNotifications()
    {
        var source = new FakeSource();
        await using var registry = new ScriptHotkeyRegistry(source, 8, () => Assert.Fail("overflow"));
        List<int> events = [];
        registry.TryRegister(Key, (key, token) => Record(1));
        Action old = source.Notify!;
        old();
        registry.TryRegister(Key, (key, token) => Record(2));
        old();
        source.Notify!();
        Assert.True(await registry.DispatchOneAsync(default));
        Assert.False(await registry.DispatchOneAsync(default));
        Assert.Equal([2], events);

        ValueTask Record(int value)
        {
            events.Add(value);
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReplacementKeepsPreviousRegistration(bool throws)
    {
        var source = new FakeSource();
        await using var registry = new ScriptHotkeyRegistry(source, 8, () => Assert.Fail("overflow"));
        int count = 0;
        registry.TryRegister(Key, (key, token) =>
        {
            count++;
            return ValueTask.CompletedTask;
        });
        source.Notify!();
        source.FailRegistration = true;
        source.ThrowOnRegistration = throws;
        if (throws)
        {
            Assert.Throws<InvalidOperationException>(() => registry.TryRegister(Key,
                (key, token) => throw new Exception("unexpected")));
        }
        else
        {
            Assert.False(registry.TryRegister(Key, (key, token) => throw new Exception("unexpected")));
        }

        source.Notify!();
        Assert.True(await registry.DispatchOneAsync(default));
        Assert.True(await registry.DispatchOneAsync(default));
        Assert.False(await registry.DispatchOneAsync(default));
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task FailedCandidateCannotReportOverflowAgainstExistingQueue()
    {
        var source = new FakeSource();
        int overflows = 0;
        await using var registry = new ScriptHotkeyRegistry(source, 1, () => overflows++);
        registry.TryRegister(Key, (key, token) => ValueTask.CompletedTask);
        source.Notify!();
        source.FailRegistration = true;
        Assert.False(registry.TryRegister(Key, (key, token) => ValueTask.CompletedTask));
        Assert.Equal(0, overflows);
        Assert.True(await registry.DispatchOneAsync(default));
    }

    [Fact]
    public async Task SuccessfulCandidatePublishesEarlyNotificationAfterReplacingOldQueue()
    {
        var source = new FakeSource();
        int overflows = 0;
        List<int> events = [];
        await using var registry = new ScriptHotkeyRegistry(source, 1, () => overflows++);
        registry.TryRegister(Key, (key, token) => Record(1));
        source.Notify!();
        source.NotifyBeforeSuccess = true;
        Assert.True(registry.TryRegister(Key, (key, token) => Record(2)));
        Assert.Equal(0, overflows);
        Assert.True(await registry.DispatchOneAsync(default));
        Assert.False(await registry.DispatchOneAsync(default));
        Assert.Equal([2], events);

        ValueTask Record(int value)
        {
            events.Add(value);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task FailedRemovalKeepsPendingUntilSuccessfulRemoval()
    {
        var source = new FakeSource();
        await using var registry = new ScriptHotkeyRegistry(source, 2, () => Assert.Fail("overflow"));
        registry.TryRegister(Key, (key, token) => ValueTask.CompletedTask);
        Action old = source.Notify!;
        old();
        source.FailRemoval = true;
        Assert.False(registry.TryUnregister(Key));
        Assert.True(await registry.DispatchOneAsync(default));
        old();
        source.FailRemoval = false;
        Assert.True(registry.TryUnregister(Key));
        old();
        Assert.False(await registry.DispatchOneAsync(default));
        Assert.True(registry.TryUnregister(Key));
    }

    [Fact]
    public async Task CleanupFailureStillPreventsLateNotificationsAndWakesWaiter()
    {
        var source = new FakeSource { FailDispose = true };
        var registry = new ScriptHotkeyRegistry(source, 2, () => Assert.Fail("overflow"));
        registry.TryRegister(Key, (key, token) => throw new Exception("unexpected"));
        Action old = source.Notify!;
        Task ready = registry.Ready;
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.DisposeAsync().AsTask());
        await ready.WaitAsync(TimeSpan.FromSeconds(5));
        old();
        Assert.False(await registry.DispatchOneAsync(default));
        await registry.DisposeAsync();
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task OverflowIsReportedWithoutChangingAcceptedNotification()
    {
        var source = new FakeSource();
        int overflows = 0;
        int calls = 0;
        await using var registry = new ScriptHotkeyRegistry(source, 1, () => overflows++);
        registry.TryRegister(Key, (key, token) =>
        {
            Assert.Equal(Key, key);
            calls++;
            return ValueTask.CompletedTask;
        });
        source.Notify!();
        source.Notify!();
        Assert.Equal(1, overflows);
        Assert.True(await registry.DispatchOneAsync(default));
        Assert.False(await registry.DispatchOneAsync(default));
        Assert.Equal(1, calls);
    }

    private sealed class FakeSource : IScriptHotkeySource
    {
        internal Action? Notify;
        internal bool FailRegistration;
        internal bool ThrowOnRegistration;
        internal bool NotifyBeforeSuccess;
        internal bool FailRemoval;
        internal bool FailDispose;
        internal int DisposeCount;

        public bool TryRegister(ScriptHotkey key, Action notify)
        {
            if (FailRegistration)
            {
                // 同期的な通知が先行しても、失敗した候補を配送しないことを確かめます。
                notify();
                if (ThrowOnRegistration)
                {
                    throw new InvalidOperationException("register");
                }

                return false;
            }

            Notify = notify;
            if (NotifyBeforeSuccess)
            {
                notify();
            }

            return true;
        }

        public bool TryUnregister(ScriptHotkey key)
        {
            if (FailRemoval)
            {
                return false;
            }

            Notify = null;
            return true;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return FailDispose
                ? ValueTask.FromException(new InvalidOperationException("dispose"))
                : ValueTask.CompletedTask;
        }
    }
}
