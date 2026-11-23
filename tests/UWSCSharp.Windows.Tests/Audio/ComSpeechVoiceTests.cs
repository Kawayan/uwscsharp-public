using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽COMだけを専用STAで動かし、音声エンジン・画面・入力を使用しません。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComSpeechVoiceTests
{
    [Theory]
    [InlineData((int)SpeechEngine.MicrosoftSpeechPlatform, "Speech.SpVoice")]
    [InlineData((int)SpeechEngine.Sapi5, "SAPI.SpVoice")]
    public async Task UsesSelectedEngineAndOneSta(int engine, string programId)
    {
        var runtime = new Runtime();
        ComSpeechVoice voice = await ComSpeechVoice.CreateAsync((SpeechEngine)engine, runtime);
        runtime.InvokeCall = (name, arguments) =>
        {
            if (name == "Speak")
            {
                Assert.Equal("日本語\0後半", Assert.IsType<string>(arguments[0]));
                AssertInteger(arguments[1], 3);
                return 42;
            }

            Assert.Equal("WaitUntilDone", name);
            AssertInteger(arguments[0], 100);
            return true;
        };
        Assert.Equal(42, await voice.SpeakAsync(new("日本語\0後半", 3)));
        Assert.True(await voice.WaitUntilDoneAsync(100));
        await voice.DisposeAsync();
        Assert.Equal(["create:" + programId, "Speak", "WaitUntilDone", "release"],
            runtime.Calls.Select(call => call.Name));
        Assert.Single(runtime.Calls.Select(call => call.Thread).Distinct());
        Assert.All(runtime.Calls, call => Assert.Equal(ApartmentState.STA, call.Apartment));
        Assert.Equal(1, runtime.Releases);
        await voice.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => voice.SpeakAsync(new("late", 1)));
    }

    [Fact]
    public async Task DispatchQueuePreservesSendAndCompletionSeparation()
    {
        var runtime = new Runtime();
        runtime.InvokeCall = (name, _) => name == "Speak" ? 7 : false;
        await using ComSpeechVoice voice = await ComSpeechVoice.CreateAsync(SpeechEngine.Sapi5, runtime);
        await using var queue = new SpeechDispatchQueue((request, token) => voice.SpeakAsync(request, token));
        await queue.Enqueue(new("hello", 1)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await voice.WaitUntilDoneAsync(0));
        Assert.Equal(["create:SAPI.SpVoice", "Speak", "WaitUntilDone"],
            runtime.Calls.Select(call => call.Name));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData(1.5)]
    [InlineData(2147483648d)]
    public async Task RejectsInvalidStreamNumber(object result)
    {
        var runtime = new Runtime { InvokeCall = (_, _) => result };
        await using ComSpeechVoice voice = await ComSpeechVoice.CreateAsync(SpeechEngine.Sapi5, runtime);
        await Assert.ThrowsAsync<InvalidDataException>(() => voice.SpeakAsync(new("text", 1)));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData(2)]
    public async Task RejectsInvalidCompletionResult(object result)
    {
        var runtime = new Runtime { InvokeCall = (_, _) => result };
        await using ComSpeechVoice voice = await ComSpeechVoice.CreateAsync(SpeechEngine.Sapi5, runtime);
        await Assert.ThrowsAsync<InvalidDataException>(() => voice.WaitUntilDoneAsync(100));
    }

    [Fact]
    public async Task ValidatesBeforeCallingCom()
    {
        var runtime = new Runtime();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ComSpeechVoice.CreateAsync((SpeechEngine)999, runtime));
        Assert.Empty(runtime.Calls);
        await using ComSpeechVoice voice = await ComSpeechVoice.CreateAsync(SpeechEngine.Sapi5, runtime);
        await Assert.ThrowsAsync<ArgumentNullException>(() => voice.SpeakAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => voice.SpeakAsync(new(null!, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => voice.WaitUntilDoneAsync(-2));
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task CancelledCreationReleasesAcquiredVoice()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { Creating = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ComSpeechVoice.CreateAsync(SpeechEngine.Sapi5, runtime, cancellation.Token));
        Assert.Equal(1, runtime.Releases);
        Assert.Equal(["create:SAPI.SpVoice", "release"], runtime.Calls.Select(call => call.Name));
    }

    [Fact]
    public async Task CreationFailureDoesNotSwitchEngine()
    {
        var failure = new InvalidOperationException("create failed");
        var runtime = new Runtime { Creating = () => throw failure };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ComSpeechVoice.CreateAsync(SpeechEngine.MicrosoftSpeechPlatform, runtime)));
        Assert.Equal("create:Speech.SpVoice", Assert.Single(runtime.Calls).Name);
        Assert.Equal(0, runtime.Releases);
    }

    [Fact]
    public async Task CancelledCreationPreservesCleanupFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        var runtime = new Runtime
        {
            Creating = cancellation.Cancel,
            Releasing = () => throw cleanupFailure
        };
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            ComSpeechVoice.CreateAsync(SpeechEngine.Sapi5, runtime, cancellation.Token));
        AggregateException flattened = failure.Flatten();
        Assert.Contains(flattened.InnerExceptions, error => error is OperationCanceledException);
        Assert.Contains(cleanupFailure, flattened.InnerExceptions);
        Assert.Equal(1, runtime.Releases);
    }

    [Fact]
    public async Task DisposalWaitsForComCallBeforeRelease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime
        {
            InvokeCall = (_, _) =>
            {
                entered.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return 1;
            }
        };
        ComSpeechVoice voice = await ComSpeechVoice.CreateAsync(SpeechEngine.Sapi5, runtime);
        Task<int> sent = voice.SpeakAsync(new("text", 1), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Task disposing = voice.DisposeAsync().AsTask();
            Assert.False(sent.IsCompleted);
            Assert.False(disposing.IsCompleted);
            Assert.Equal(0, runtime.Releases);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sent);
            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, runtime.Releases);
        }
        finally
        {
            release.Set();
            await voice.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvocationAndReleaseFailuresAreObservable()
    {
        var invokeFailure = new InvalidOperationException("invoke failed");
        var releaseFailure = new InvalidOperationException("release failed");
        var runtime = new Runtime
        {
            InvokeCall = (_, _) => throw invokeFailure,
            Releasing = () => throw releaseFailure
        };
        ComSpeechVoice voice = await ComSpeechVoice.CreateAsync(SpeechEngine.Sapi5, runtime);
        Assert.Same(invokeFailure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            voice.SpeakAsync(new("text", 1))));
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            voice.DisposeAsync().AsTask());
        Assert.Contains(releaseFailure, failure.InnerExceptions);
        Assert.Equal(1, runtime.Releases);
    }

    [Theory]
    [InlineData(unchecked((int)0x80040154))]
    [InlineData(unchecked((int)0x800401F3))]
    public async Task PreferredEngineFallsBackWhenClassIsUnavailable(int hresult)
    {
        var runtime = new Runtime
        {
            CreateFailure = name => name == "Speech.SpVoice"
                ? new COMException("未登録", hresult) : null
        };
        await using ComSpeechVoice voice = await ComSpeechVoice.CreateAsync(
            SpeechEngine.PreferSpeechPlatform, runtime);

        Assert.Equal(["create:Speech.SpVoice", "create:SAPI.SpVoice"],
            runtime.Calls.Select(call => call.Name));
    }

    [Fact]
    public async Task OtherCreationFailuresDoNotSwitchEngines()
    {
        var failure = new COMException("アクセス拒否", unchecked((int)0x80070005));
        var runtime = new Runtime { CreateFailure = _ => failure };

        Assert.Same(failure, await Assert.ThrowsAsync<COMException>(() =>
            ComSpeechVoice.CreateAsync(SpeechEngine.PreferSpeechPlatform, runtime)));
        Assert.Equal("create:Speech.SpVoice", Assert.Single(runtime.Calls).Name);
    }

    [Fact]
    public async Task FailedFallbackPreservesBothCreationErrors()
    {
        var missing = new COMException("未登録", unchecked((int)0x80040154));
        var failed = new COMException("作成失敗", unchecked((int)0x80004005));
        var runtime = new Runtime
        {
            CreateFailure = name => name == "Speech.SpVoice" ? missing : failed
        };

        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() =>
            ComSpeechVoice.CreateAsync(SpeechEngine.PreferSpeechPlatform, runtime));
        Assert.Equal([missing, failed], error.InnerExceptions);
    }

    [Fact]
    public async Task PreferredEngineDoesNotSwitchAfterSpeechFailure()
    {
        var failure = new COMException("送信失敗", unchecked((int)0x80040154));
        var runtime = new Runtime { InvokeCall = (_, _) => throw failure };
        await using ComSpeechVoice voice = await ComSpeechVoice.CreateAsync(
            SpeechEngine.PreferSpeechPlatform, runtime);

        Assert.Same(failure, await Assert.ThrowsAsync<COMException>(() =>
            voice.SpeakAsync(new SpeechDispatchRequest("test", 1))));
        Assert.Equal(["create:Speech.SpVoice", "Speak"],
            runtime.Calls.Select(call => call.Name));
    }


    private static void AssertInteger(object? argument, int expected)
    {
        ComVariantValue value = Assert.IsType<ComVariantValue>(argument);
        Assert.Equal((ushort)3, value.Type);
        Assert.Equal(expected, Assert.IsType<int>(value.Value));
    }

    private sealed class Runtime : IComRuntime
    {
        private readonly object voice = new();
        internal readonly ConcurrentQueue<(string Name, int Thread, ApartmentState Apartment)> Calls = new();
        internal Action? Creating;
        internal Func<string, Exception?>? CreateFailure;
        internal Action? Releasing;
        internal Func<string, object?[], object?>? InvokeCall;
        internal int Releases;

        private void Record(string name) => Calls.Enqueue((name,
            Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));

        public object Create(string name)
        {
            Record("create:" + name);
            Creating?.Invoke();
            if (CreateFailure?.Invoke(name) is { } failure)
            {
                throw failure;
            }
            return voice;
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            Assert.Same(voice, target);
            Assert.False(assign);
            Record(name);
            return InvokeCall?.Invoke(name, arguments);
        }

        public void Release(object target)
        {
            Assert.Same(voice, target);
            Record("release");
            Interlocked.Increment(ref Releases);
            Releasing?.Invoke();
        }
    }
}
