using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 発声と入力は偽backendだけで確認し、実画面・実エンジンを使いません。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRunLifetimeTests
{
    [Fact]
    public async Task WorkersShareOneEngineAndOnlyRootReleasesIt()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime);
        IWindowsAutomationSession worker = root.Fork();
        await Speech(root).SendSpeechAsync(SpeechEngine.Sapi5, new("root", 1));
        await Speech(worker).SendSpeechAsync(SpeechEngine.Sapi5, new("worker", 1));
        await worker.DisposeAsync();
        Assert.Equal(1, runtime.Creates);
        Assert.Equal(0, runtime.Releases);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = Speech(root).SendSpeechAsync(SpeechEngine.MicrosoftSpeechPlatform, new("change", 1));
        });
        await root.DisposeAsync();
        Assert.Equal(1, runtime.Purges);
        Assert.Equal(1, runtime.Releases);
        Assert.Throws<ObjectDisposedException>(() => { _ = Speech(worker).WaitForSpeechAsync(); });
    }

    [Fact]
    public async Task SeparateRunsKeepSeparateEngines()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession first = Create(runtime);
        await using IWindowsAutomationSession second = Create(runtime);
        await Speech(first).SendSpeechAsync(SpeechEngine.Sapi5, new("first", 1));
        await Speech(second).SendSpeechAsync(SpeechEngine.Sapi5, new("second", 1));
        await first.DisposeAsync();
        Assert.Equal(2, runtime.Creates);
        Assert.Equal(1, runtime.Releases);
        await Speech(second).SendSpeechAsync(SpeechEngine.Sapi5, new("still alive", 1));
        await second.DisposeAsync();
        Assert.Equal(2, runtime.Releases);
    }

    [Fact]
    public async Task RootShutdownCancelsSpeechWaitBeforeWaitingForWorkerExit()
    {
        var runtime = new Runtime { Complete = false };
        IWindowsAutomationSession root = Create(runtime);
        IWindowsAutomationSession worker = root.Fork();
        try
        {
            await Speech(worker).SendSpeechAsync(SpeechEngine.Sapi5, new("pending", 1));
            Task wait = Speech(worker).WaitForSpeechAsync();
            await runtime.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task workerExit = ExitAfterWait();
            await root.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await workerExit;
            Assert.Equal(1, runtime.Releases);

            async Task ExitAfterWait()
            {
                try
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
                }
                finally
                {
                    await worker.DisposeAsync();
                }
            }
        }
        finally
        {
            await worker.DisposeAsync();
            await root.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkerCancellationOnlyCancelsItsWait()
    {
        var runtime = new Runtime { Complete = false };
        using var stop = new CancellationTokenSource();
        await using IWindowsAutomationSession root = Create(runtime);
        await using IWindowsAutomationSession worker = root.Fork();
        worker.ConfigurePause(null, stop.Token);
        await Speech(worker).SendSpeechAsync(SpeechEngine.Sapi5, new("shared", 1));
        Task wait = Speech(worker).WaitForSpeechAsync();
        await runtime.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(0, runtime.Purges);
        Assert.Equal(0, runtime.Releases);
        await Speech(root).SendSpeechAsync(SpeechEngine.Sapi5, new("root continues", 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootCancellationStopsExistingEngineAndRejectsLaterRequests(bool firstToken)
    {
        var runtime = new Runtime();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        await using IWindowsAutomationSession root = Create(runtime);
        root.ConfigurePause(null, first.Token);
        root.ConfigurePause(null, second.Token);
        root.ConfigurePause(null, second.Token);
        await Speech(root).SendSpeechAsync(SpeechEngine.Sapi5, new("started", 1));
        (firstToken ? first : second).Cancel();
        await runtime.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = Speech(root).SendSpeechAsync(SpeechEngine.Sapi5, new("late", 1));
        });
        await root.DisposeAsync();
        Assert.Equal(1, runtime.Purges);
        Assert.Equal(1, runtime.Releases);
    }

    [Fact]
    public async Task RootCancellationBeforeUseDoesNotCreateEngine()
    {
        var runtime = new Runtime();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await using IWindowsAutomationSession root = Create(runtime);
        root.ConfigurePause(null, stop.Token);
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = Speech(root).SendSpeechAsync(SpeechEngine.Sapi5, new("late", 1));
        });
        await root.DisposeAsync();
        Assert.Equal(0, runtime.Creates);
    }

    [Fact]
    public async Task RootCleanupPreservesStopAndReleaseFailures()
    {
        var runtime = new Runtime { FailCleanup = true };
        IWindowsAutomationSession root = Create(runtime);
        await Speech(root).SendSpeechAsync(SpeechEngine.Sapi5, new("started", 1));
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => root.DisposeAsync().AsTask());
        Assert.Contains(failure.Flatten().InnerExceptions, error => error.Message == "purge");
        Assert.Contains(failure.Flatten().InnerExceptions, error => error.Message == "release");
        Assert.Equal(1, runtime.Releases);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 3)]
    [InlineData(true, true, 3)]
    public async Task SubmitsFlagsAndWaitsOnlyForSynchronousSpeech(
        bool parallel, bool interrupt, int flags)
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime);
        await Speech(root).SubmitSpeechAsync(
            SpeechEngine.Sapi5, new SpeakRequest("test", parallel, interrupt));

        Assert.Equal(flags, Assert.Single(runtime.SpeechFlags));
        Assert.Equal(!parallel, runtime.WaitEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task EmptyArgumentListDoesNotInitializeSpeechEngine()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime);
        await Speech(root).SubmitSpeechAsync(SpeechEngine.Sapi5, SpeakRequest.Read([]));

        Assert.Equal(0, runtime.Creates);
        Assert.Empty(runtime.SpeechFlags);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyNoArgumentAndParallelInterruptDiscardPending(
        bool? interruptParallel)
    {
        using var release = new ManualResetEventSlim();
        var runtime = new Runtime { SendGate = release };
        await using IWindowsAutomationSession root = Create(runtime);
        ISpeechEngineSession speech = Speech(root);
        Task sending = speech.SubmitSpeechAsync(
            SpeechEngine.Sapi5, new SpeakRequest("first", true, false));
        Task pending;
        Task later;
        try
        {
            await runtime.Sending.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pending = speech.SubmitSpeechAsync(
                SpeechEngine.Sapi5, new SpeakRequest("pending", true, false));
            SpeakRequest request = interruptParallel is bool parallel
                ? new("interrupt", parallel, true) : SpeakRequest.Read([]);
            later = speech.SubmitSpeechAsync(SpeechEngine.Sapi5, request);
            if (interruptParallel != false)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                Assert.False(pending.IsCompleted);
            }
            Assert.False(sending.IsCompleted);
            Assert.Equal(0, runtime.Purges);
        }
        finally
        {
            release.Set();
        }

        await sending;
        await later;
        if (interruptParallel == false)
        {
            await pending;
        }
        int[] expected = interruptParallel switch
        {
            null => [1],
            false => [1, 1, 3],
            true => [1, 3]
        };
        Assert.Equal(expected, runtime.SpeechFlags);
    }

    [Theory]
    [InlineData(false, "Speech.SpVoice")]
    [InlineData(true, "SAPI.SpVoice")]
    public async Task OnlySapiControlsInitialSelectionForTheWholeRun(
        bool onlySapi, string programId)
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime);
        await using IWindowsAutomationSession worker = root.Fork();
        await Speech(root).SubmitSpeechAsync(onlySapi, new SpeakRequest("root", true, false));
        await Speech(worker).SubmitSpeechAsync(onlySapi, new SpeakRequest("worker", true, false));

        await Speech(root).WaitForSpeechAsync();
        Assert.Equal(programId, Assert.Single(runtime.ProgramIds));
    }


    [Fact]
    public async Task ParallelFailureIsReportedOnceAfterReturning()
    {
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("検証用の送信失敗です。");
        var runtime = new Runtime { SendGate = release, SendFailure = failure };
        await using IWindowsAutomationSession root = Create(runtime);
        ISpeechEngineSession speech = Speech(root);
        try
        {
            await speech.SubmitSpeechAsync(true, new("queued", true, false))
                .WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.Sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(root.TakeAutomationFailure());
        }
        finally
        {
            release.Set();
        }

        await speech.WaitForSpeechAsync().WaitAsync(TimeSpan.FromSeconds(5));
        WindowsAutomationFailure reported = Assert.IsType<WindowsAutomationFailure>(
            root.TakeAutomationFailure());
        Assert.Equal("SPEAK", reported.FunctionName);
        Assert.Same(failure, reported.Error);
        Assert.Null(root.TakeAutomationFailure());
    }

    [Fact]
    public async Task UnreadParallelFailureIsPreservedAtRootDisposal()
    {
        var failure = new InvalidOperationException("検証用の送信失敗です。");
        var runtime = new Runtime { SendFailure = failure };
        IWindowsAutomationSession root = Create(runtime);
        ISpeechEngineSession speech = Speech(root);
        await speech.SubmitSpeechAsync(true, new("queued", true, false));
        await speech.WaitForSpeechAsync();

        Exception actual = await Assert.ThrowsAnyAsync<Exception>(
            () => root.DisposeAsync().AsTask());
        if (actual is AggregateException group)
        {
            Assert.Contains(failure, group.Flatten().InnerExceptions);
        }
        else
        {
            Assert.Same(failure, actual);
        }
    }

    [Fact]
    public async Task SynchronousFailureIsNotReportedTwice()
    {
        var failure = new InvalidOperationException("検証用の送信失敗です。");
        var runtime = new Runtime { SendFailure = failure };
        await using IWindowsAutomationSession root = Create(runtime);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Speech(root).SubmitSpeechAsync(true, new("sync", false, false))));
        Assert.Null(root.TakeAutomationFailure());
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearingPendingSpeechKeepsSynchronousSendAndWait(bool interrupt)
    {
        using var release = new ManualResetEventSlim();
        var runtime = new Runtime { SendGate = release };
        await using IWindowsAutomationSession root = Create(runtime);
        await using IWindowsAutomationSession worker = root.Fork();
        IScriptSpeechSession script = Assert.IsAssignableFrom<IScriptSpeechSession>(worker);
        Task synchronous;
        Task waiting;
        try
        {
            await script.SubmitSpeechAsync(true, new("active", true, false), default);
            await runtime.Sending.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await script.SubmitSpeechAsync(true, new("discard", true, false), default);
            synchronous = script.SubmitSpeechAsync(true, new("sync", false, false), default);
            waiting = Speech(root).WaitForSpeechAsync();

            SpeakRequest clear = interrupt ? new("interrupt", true, true) : new();
            await script.SubmitSpeechAsync(true, clear, default);
            Assert.False(synchronous.IsCompleted);
            Assert.False(waiting.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await Task.WhenAll(synchronous, waiting).WaitAsync(TimeSpan.FromSeconds(10));
        await Speech(root).WaitForSpeechAsync().WaitAsync(TimeSpan.FromSeconds(10));
        int[] expected = interrupt ? [1, 1, 3] : [1, 1];
        Assert.Equal(expected, runtime.SpeechFlags);
    }

    [Theory]
    [InlineData("SPEAK(\"first\")", "Speech.SpVoice")]
    [InlineData("OPTION ONLYSAPI\nSPEAK(\"first\")", "SAPI.SpVoice")]
    [InlineData("SPEAK(\"first\")\nOPTION ONLYSAPI\nSPEAK(\"second\")", "Speech.SpVoice")]
    public async Task PublicScriptSelectsEngineAtFirstUse(string script, string programId)
    {
        var runtime = new Runtime();
        await new Interpreter(windows: CreateAutomation(runtime)).RunAsync(script, _ => { });
        Assert.Equal(programId, Assert.Single(runtime.ProgramIds));
    }

    [Fact]
    public async Task PublicScriptRejectsExtraArgumentsBeforeEvaluation()
    {
        var runtime = new Runtime();
        var output = new List<string>();
        var interpreter = new Interpreter(windows: CreateAutomation(runtime));
        await Assert.ThrowsAsync<ScriptException>(() => interpreter.RunAsync("""
            FUNCTION Touch()
                PRINT "evaluated"
            FEND
            SPEAK(Touch(), FALSE, FALSE, FALSE)
            """, output.Add));
        Assert.Empty(output);
        Assert.Empty(runtime.ProgramIds);
    }

    [Fact]
    public async Task PublicScriptWithoutArgumentsDoesNotCreateEngine()
    {
        var runtime = new Runtime();
        await new Interpreter(windows: CreateAutomation(runtime)).RunAsync("SPEAK()", _ => { });
        Assert.Empty(runtime.ProgramIds);
    }

    [Theory]
    [InlineData("FALSE")]
    [InlineData("TRUE")]
    public async Task PublicScriptReportsSendFailure(string parallel)
    {
        var runtime = new Runtime { SendFailure = new InvalidOperationException("send failed") };
        var interpreter = new Interpreter(windows: CreateAutomation(runtime));
        ScriptException failure = await Assert.ThrowsAsync<ScriptException>(() =>
            interpreter.RunAsync($"SPEAK(\"test\", {parallel})\nSLEEP(1)", _ => { }));
        Assert.Contains("send failed", failure.Message);
    }

    [Fact]
    public async Task CancellingPublicSynchronousSpeechStopsAndReleasesEngine()
    {
        var runtime = new Runtime { Complete = false };
        var interpreter = new Interpreter(windows: CreateAutomation(runtime));
        using var cancellation = new CancellationTokenSource();
        List<string> output = [];
        Task run = interpreter.RunAsync(
            "SPEAK(\"読み上げ\")\nPRINT \"終了\"", output.Add, cancellation.Token);
        try
        {
            await runtime.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(run.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(output);
        Assert.Equal([1, 3], runtime.SpeechFlags);
        Assert.Equal(1, runtime.Purges);
        Assert.Equal(1, runtime.Releases);
    }

    private static ISpeechEngineSession Speech(IWindowsAutomationSession session)
        => Assert.IsAssignableFrom<ISpeechEngineSession>(session);

    private static IWindowsAutomationSession Create(Runtime runtime)
        => CreateAutomation(runtime).CreateSession();

    private static WindowsAutomation CreateAutomation(Runtime runtime)
        => new(_ => throw new InvalidOperationException("物理入力は禁止です。"),
            comRuntime: runtime, swappedMouseButtons: false);

    private sealed class Runtime : IComRuntime
    {
        internal int Creates;
        internal int Releases;
        internal int Purges;
        internal readonly List<string> ProgramIds = [];
        internal List<int> SpeechFlags
        {
            get;
        } = [];
        internal ManualResetEventSlim? SendGate;
        internal Exception? SendFailure;
        internal TaskCompletionSource Sending
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Complete = true;
        internal bool FailCleanup;
        internal TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object Create(string name)
        {
            ProgramIds.Add(name);
            Interlocked.Increment(ref Creates);
            return new object();
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            if (name == "WaitUntilDone")
            {
                WaitEntered.TrySetResult();
                return Complete;
            }

            Assert.Equal("Speak", name);
            ComVariantValue flags = Assert.IsType<ComVariantValue>(arguments[1]);
            SpeechFlags.Add(Assert.IsType<int>(flags.Value));
            if (SendGate is not null)
            {
                Sending.TrySetResult();
                Assert.True(SendGate.Wait(TimeSpan.FromSeconds(10)));
            }
            if (SendFailure is { } failure && !Equals(arguments[0], ""))
            {
                throw failure;
            }
            if (Equals(flags.Value, 3))
            {
                Interlocked.Increment(ref Purges);
                if (FailCleanup)
                {
                    throw new InvalidOperationException("purge");
                }
            }

            return 1;
        }

        public void Release(object target)
        {
            Interlocked.Increment(ref Releases);
            Released.TrySetResult();
            if (FailCleanup)
            {
                throw new InvalidDataException("release");
            }
        }
    }
}
