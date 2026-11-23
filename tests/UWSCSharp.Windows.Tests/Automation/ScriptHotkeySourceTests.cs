using System.Collections.Concurrent;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptHotkeySourceTests
{
    [Fact]
    public async Task EmergencyReservationNeverReachesThePlatform()
    {
        using var platform = new Platform();
        await using var source = new WindowsScriptHotkeySource(platform);
        var previous = new ScriptHotkey(65, 2);
        Assert.True(source.TryRegister(previous, () => { }));
        int id = Assert.Single(platform.Registered);
        Assert.False(source.TryRegister(new(122, 7), () => { }));
        Assert.Equal(previous, platform.LastKey);
        Assert.Equal(id, Assert.Single(platform.Registered));
        Assert.Empty(platform.Removed);
        Assert.True(source.TryRegister(new(122, 15), () => { }));
        Assert.Equal(new ScriptHotkey(122, 15), platform.LastKey);
    }

    [Fact]
    public async Task FailedRegistrationsDoNotExhaustIdentifiers()
    {
        using var platform = new Platform { FailRegistration = true };
        await using var source = new WindowsScriptHotkeySource(platform);
        for (int index = 0; index <= 0xBFFF; index++)
        {
            Assert.False(source.TryRegister(new ScriptHotkey(65, 0), () => { }));
        }

        platform.FailRegistration = false;
        Assert.True(source.TryRegister(new ScriptHotkey(66, 0), () => { }));
        Assert.Equal(1, Assert.Single(platform.Registered));
    }

    [Fact]
    public async Task ModifierOnlyRegistrationReachesPlatform()
    {
        using var platform = new Platform();
        await using var source = new WindowsScriptHotkeySource(platform);
        Assert.True(source.TryRegister(new ScriptHotkey(0, 6), () => { }));
        Assert.Equal(new ScriptHotkey(0, 6), platform.LastKey);
        Assert.True(source.TryUnregister(new ScriptHotkey(0, 6)));
    }

    [Fact]
    public async Task ReplacementNeverRoutesQueuedOldNotificationsToNewCallback()
    {
        using var platform = new Platform();
        await using var source = new WindowsScriptHotkeySource(platform);
        var key = new ScriptHotkey(65, 0);
        int newCalls = 0;
        int oldCalls = 0;
        Assert.True(source.TryRegister(key, () => Interlocked.Increment(ref oldCalls)));
        platform.AllowReads.Reset();
        platform.Publish(Assert.Single(platform.Registered));
        await platform.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool> replacement = Task.Run(() =>
            source.TryRegister(key, () => Interlocked.Increment(ref newCalls)));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (source.PendingRequestCount == 0)
            {
                await Task.Delay(1, timeout.Token);
            }

            for (int index = 0; index < 255; index++)
            {
                platform.Publish(platform.Registered.Single());
            }
        }
        finally
        {
            platform.AllowReads.Set();
        }

        Assert.False(await replacement.WaitAsync(TimeSpan.FromSeconds(5)));
        for (int index = 0; index < 5; index++)
        {
            source.TryUnregister(new ScriptHotkey(66, 0));
        }

        Assert.Equal(0, newCalls);
        Assert.Equal(256, oldCalls);
        platform.Publish(platform.Registered.Single());
        Assert.True(source.TryUnregister(new ScriptHotkey(66, 0)));
        Assert.Equal(257, oldCalls);
        Assert.True(source.TryRegister(key, () => Interlocked.Increment(ref newCalls)));
        platform.Publish(platform.Registered.Single());
        Assert.True(source.TryUnregister(new ScriptHotkey(66, 0)));
        Assert.Equal(1, newCalls);
    }

    [Fact]
    public async Task RegistrationReplacementAndRemovalUseOwnedThread()
    {
        using var platform = new Platform();
        var source = new WindowsScriptHotkeySource(platform);
        var key = new ScriptHotkey(65, 2);
        var calls = new ConcurrentQueue<string>();
        Assert.True(source.TryRegister(key, () => calls.Enqueue("first")));
        int firstId = Assert.Single(platform.Registered);
        platform.Publish(firstId);
        Assert.True(source.TryRegister(key, () => calls.Enqueue("second")));
        platform.Publish(firstId);
        Assert.True(source.TryUnregister(new ScriptHotkey(66, 0)));
        Assert.Equal(["first", "second"], calls);
        Assert.True(source.TryUnregister(key));
        platform.Publish(firstId);
        Assert.True(source.TryRegister(key, () => calls.Enqueue("third")));
        Assert.Equal(2, platform.Registered.Count);
        Assert.NotEqual(firstId, platform.Registered.Last());
        Assert.Equal(["first", "second"], calls);
        await source.DisposeAsync();
        await source.DisposeAsync();
        Assert.Equal(platform.Registered, platform.Removed);
        Assert.Single(platform.Threads.Distinct());
        Assert.Equal(1, platform.Shutdowns);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, platform.Threads);
    }

    [Fact]
    public async Task FailedRemovalRetainsNotificationUntilSuccessfulRemoval()
    {
        using var platform = new Platform();
        await using var source = new WindowsScriptHotkeySource(platform);
        var key = new ScriptHotkey(65, 0);
        int calls = 0;
        Assert.True(source.TryRegister(key, () => calls++));
        platform.FailRemoval = true;
        Assert.False(source.TryUnregister(key));
        platform.Publish(Assert.Single(platform.Registered));
        platform.FailRemoval = false;
        Assert.True(source.TryUnregister(key));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NotificationFailureCompletesRequestsAndReleasesRegistrations()
    {
        using var platform = new Platform();
        var source = new WindowsScriptHotkeySource(platform);
        Assert.True(source.TryRegister(new ScriptHotkey(65, 0),
            () => throw new InvalidOperationException("notification")));
        platform.Publish(Assert.Single(platform.Registered));
        Task registration = Task.Run(() => source.TryRegister(
            new ScriptHotkey(66, 0), () => { }));
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
            registration.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("notification", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.DisposeAsync().AsTask());
        Assert.Equal(platform.Registered, platform.Removed);
        Assert.Contains("notification", source.Failure!.Message);
    }

    [Fact]
    public async Task InitializationFailureIsReportedByRegistrationAndDisposal()
    {
        using var platform = new Platform { FailInitialization = true };
        var source = new WindowsScriptHotkeySource(platform);
        Assert.Throws<InvalidOperationException>(() =>
            source.TryRegister(new ScriptHotkey(65, 0), () => { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.DisposeAsync().AsTask());
        Assert.Equal(1, platform.Shutdowns);
    }

    private sealed class Platform : IScriptHotkeyPlatform, IDisposable
    {
        private readonly AutoResetEvent input = new(false);
        private readonly ConcurrentQueue<int> messages = new();
        internal readonly ConcurrentQueue<int> Registered = new();
        internal readonly ConcurrentQueue<int> Removed = new();
        internal readonly ConcurrentQueue<int> Threads = new();
        internal bool FailInitialization;
        internal bool FailRegistration;
        internal ScriptHotkey LastKey;
        internal int Shutdowns;
        internal volatile bool FailRemoval;
        internal readonly ManualResetEventSlim AllowReads = new(true);
        internal readonly TaskCompletionSource ReadBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Initialize()
        {
            if (FailInitialization)
            {
                throw new InvalidOperationException("initialize");
            }

            Track();
        }

        public bool Register(int id, ScriptHotkey key)
        {
            Track();
            if (FailRegistration)
            {
                return false;
            }

            LastKey = key;
            Registered.Enqueue(id);
            return true;
        }

        public bool Unregister(int id)
        {
            Track();
            if (FailRemoval)
            {
                return false;
            }

            Removed.Enqueue(id);
            return true;
        }

        public bool TryRead(out int id)
        {
            Track();
            if (!AllowReads.IsSet)
            {
                ReadBlocked.TrySetResult();
                if (!AllowReads.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("read gate");
                }
            }

            return messages.TryDequeue(out id);
        }

        public void Wait(AutoResetEvent work)
        {
            Track();
            WaitHandle.WaitAny([work, input]);
        }

        internal void Publish(int id)
        {
            messages.Enqueue(id);
            input.Set();
        }

        private void Track() => Threads.Enqueue(Environment.CurrentManagedThreadId);

        public void Shutdown()
        {
            Track();
            Shutdowns++;
        }

        public void Dispose()
        {
            input.Dispose();
            AllowReads.Dispose();
        }
    }
}
