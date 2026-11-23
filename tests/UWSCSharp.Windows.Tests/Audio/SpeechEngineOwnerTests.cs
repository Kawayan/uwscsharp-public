using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽COMだけを使い、実エンジン・音声・画面を起動しません。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechEngineOwnerTests
{
    [Fact]
    public async Task UnusedOwnerDoesNotCreateEngine()
    {
        var runtime = new Runtime();
        var owner = new SpeechEngineOwner(SpeechEngine.Sapi5, runtime);
        await owner.WaitUntilDoneAsync();
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        Assert.Empty(runtime.Calls);
        Assert.Throws<ObjectDisposedException>(() => { _ = owner.Enqueue(new("late", 1)); });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpeechEngineOwner((SpeechEngine)99));
    }

    [Fact]
    public async Task ReusesEngineAndPurgesBeforeRelease()
    {
        var runtime = new Runtime();
        var owner = new SpeechEngineOwner(SpeechEngine.Sapi5, runtime);
        await owner.Enqueue(new("first", 1));
        await owner.Enqueue(new("second", 17));
        await owner.DisposeAsync();
        Assert.Equal(["create:SAPI.SpVoice", "Speak:first:1", "Speak:second:17",
            "Speak::3", "release"], runtime.Calls);
        Assert.Single(runtime.Threads.Distinct());
    }

    [Fact]
    public async Task ShutdownDrainsActiveCallCancelsPendingAndThenPurges()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var runtime = new Runtime
        {
            Speaking = text =>
            {
                if (text == "active")
                {
                    entered.TrySetResult();
                    Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
                }
            }
        };
        var owner = new SpeechEngineOwner(SpeechEngine.Sapi5, runtime);
        Task active = owner.Enqueue(new("active", 1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task pending = owner.Enqueue(new("pending", 1));
            Task completion = owner.WaitUntilDoneAsync();
            Task closing = owner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.Same(closing, owner.DisposeAsync().AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
            Assert.Throws<ObjectDisposedException>(() => { _ = owner.Enqueue(new("late", 1)); });
            Assert.DoesNotContain("release", runtime.Calls);
            resume.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["create:SAPI.SpVoice", "Speak:active:1", "Speak::3", "release"], runtime.Calls);
        }
        finally
        {
            resume.Set();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShutdownDuringCreationReleasesWithoutSpeaking()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var runtime = new Runtime
        {
            Creating = () =>
            {
                entered.TrySetResult();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        var owner = new SpeechEngineOwner(SpeechEngine.Sapi5, runtime);
        Task request = owner.Enqueue(new("pending", 1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task closing = owner.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            resume.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["create:SAPI.SpVoice", "release"], runtime.Calls);
        }
        finally
        {
            resume.Set();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreationFailureIsCachedWithoutFallbackOrRetry()
    {
        var failure = new InvalidOperationException("create");
        var runtime = new Runtime { Creating = () => throw failure };
        var owner = new SpeechEngineOwner(SpeechEngine.MicrosoftSpeechPlatform, runtime);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.Enqueue(new("first", 1))));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.Enqueue(new("second", 1))));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => owner.WaitUntilDoneAsync()));
        await owner.DisposeAsync();
        Assert.Equal(["create:Speech.SpVoice"], runtime.Calls);
    }

    [Fact]
    public async Task PurgeAndReleaseFailuresBothRemainObservable()
    {
        var purge = new InvalidOperationException("purge");
        var release = new InvalidDataException("release");
        var runtime = new Runtime
        {
            Speaking = text =>
            {
                if (text.Length == 0)
                {
                    throw purge;
                }
            },
            Releasing = () => throw release
        };
        var owner = new SpeechEngineOwner(SpeechEngine.Sapi5, runtime);
        await owner.Enqueue(new("first", 1));
        Task closing = owner.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => closing);
        Assert.Contains(purge, closing.Exception!.Flatten().InnerExceptions);
        Assert.Contains(release, closing.Exception.Flatten().InnerExceptions);
        Assert.Same(closing, owner.DisposeAsync().AsTask());
        Assert.Equal("release", runtime.Calls.Last());
    }

    [Fact]
    public async Task CompletionWaitStartsAfterQueuedSendAndPollsUntilDone()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        int polls = 0;
        var runtime = new Runtime
        {
            Speaking = text =>
            {
                if (text == "pending")
                {
                    entered.TrySetResult();
                    Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
                }
            },
            Waiting = () => ++polls == 2
        };
        await using var owner = new SpeechEngineOwner(SpeechEngine.Sapi5, runtime);
        Task send = owner.Enqueue(new("pending", 1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task done = owner.WaitUntilDoneAsync();
            Assert.False(done.IsCompleted);
            Assert.DoesNotContain("wait", runtime.Calls);
            resume.Set();
            await send;
            await done.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, polls);
        }
        finally
        {
            resume.Set();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelsWaitWithoutReleasingAnActiveComCall(bool closeOwner)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime
        {
            Waiting = () =>
            {
                entered.TrySetResult();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
                return false;
            }
        };
        var owner = new SpeechEngineOwner(SpeechEngine.Sapi5, runtime);
        await owner.Enqueue(new("first", 1));
        Task done = owner.WaitUntilDoneAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task? closing = closeOwner ? owner.DisposeAsync().AsTask() : null;
            cancellation.Cancel();
            Assert.False(done.IsCompleted);
            Assert.DoesNotContain("release", runtime.Calls);
            resume.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => done);
            if (closing is not null)
            {
                await closing.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Throws<ObjectDisposedException>(() => { _ = owner.WaitUntilDoneAsync(); });
            }
            else
            {
                Assert.DoesNotContain("Speak::3", runtime.Calls);
                await owner.Enqueue(new("after cancellation", 1));
            }
        }
        finally
        {
            resume.Set();
            await owner.DisposeAsync();
        }
    }

    [Fact]
    public async Task WaitFailureDoesNotPreventFurtherSendsOrCleanup()
    {
        var failure = new InvalidDataException("wait");
        var runtime = new Runtime { Waiting = () => throw failure };
        var owner = new SpeechEngineOwner(SpeechEngine.Sapi5, runtime);
        await owner.Enqueue(new("first", 1));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(
            () => owner.WaitUntilDoneAsync()));
        await owner.Enqueue(new("second", 1));
        await owner.DisposeAsync();
        Assert.Equal("release", runtime.Calls.Last());
    }

    private sealed class Runtime : IComRuntime
    {
        internal ConcurrentQueue<string> Calls { get; } = new();
        internal ConcurrentQueue<int> Threads { get; } = new();
        internal Action? Creating
        {
            get; init;
        }
        internal Action<string>? Speaking
        {
            get; init;
        }
        internal Action? Releasing
        {
            get; init;
        }
        internal Func<bool>? Waiting
        {
            get; init;
        }

        public object Create(string name)
        {
            Record("create:" + name);
            Creating?.Invoke();
            return new object();
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            if (name == "WaitUntilDone")
            {
                ComVariantValue interval = Assert.IsType<ComVariantValue>(arguments[0]);
                Assert.Equal(100, interval.Value);
                Record("wait");
                return Waiting?.Invoke() ?? true;
            }

            string text = Assert.IsType<string>(arguments[0]);
            ComVariantValue flags = Assert.IsType<ComVariantValue>(arguments[1]);
            Record($"{name}:{text}:{flags.Value}");
            Speaking?.Invoke(text);
            return 1;
        }

        public void Release(object target)
        {
            Record("release");
            Releasing?.Invoke();
        }

        private void Record(string text)
        {
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Calls.Enqueue(text);
            Threads.Enqueue(Environment.CurrentManagedThreadId);
        }
    }
}
