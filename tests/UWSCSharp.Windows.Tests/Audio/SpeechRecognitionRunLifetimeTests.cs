using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽COMだけで実行寿命を確認し、認識器・入力・画面は使いません。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionRunLifetimeTests
{
    [Fact]
    public async Task WorkersShareRecognizerAndOnlyRootReleasesIt()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime);
        IWindowsAutomationSession worker = root.Fork();
        await Recognition(root).StartRecognitionAsync();
        await Recognition(worker).StartRecognitionAsync();
        await worker.DisposeAsync();
        Assert.Equal(1, runtime.Contexts);
        Assert.Equal(0, runtime.Releases);
        await Recognition(root).StopRecognitionAsync();
        await root.DisposeAsync();
        Assert.Equal(5, runtime.Releases);
        Assert.Equal(1, runtime.Disconnects);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            Recognition(worker).ReadRecognitionAsync(false));
    }

    [Fact]
    public async Task SeparateRunsKeepSeparateRecognizers()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession first = Create(runtime);
        await using IWindowsAutomationSession second = Create(runtime);
        await Recognition(first).StartRecognitionAsync();
        await Recognition(second).StartRecognitionAsync();
        await first.DisposeAsync();
        Assert.Equal(2, runtime.Contexts);
        Assert.Equal(5, runtime.Releases);
        await Recognition(second).StartRecognitionAsync();
        await second.DisposeAsync();
        Assert.Equal(10, runtime.Releases);
    }

    [Fact]
    public async Task RootShutdownUnblocksResultWaitBeforeWaitingForWorkerExit()
    {
        var runtime = new Runtime();
        IWindowsAutomationSession root = Create(runtime);
        IWindowsAutomationSession worker = root.Fork();
        try
        {
            await Recognition(worker).StartRecognitionAsync();
            Task<string> result = Recognition(worker).ReadRecognitionAsync(true);
            Task exit = ExitWorkerAsync();
            await root.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await exit;
            Assert.Equal(5, runtime.Releases);

            async Task ExitWorkerAsync()
            {
                try
                {
                    await Assert.ThrowsAsync<ObjectDisposedException>(() => result);
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
    public async Task WorkerCancellationOnlyCancelsItsResultWait()
    {
        var runtime = new Runtime();
        using var stop = new CancellationTokenSource();
        await using IWindowsAutomationSession root = Create(runtime);
        await using IWindowsAutomationSession worker = root.Fork();
        worker.ConfigurePause(null, stop.Token);
        await Recognition(worker).StartRecognitionAsync();
        Task<string> result = Recognition(worker).ReadRecognitionAsync(true);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        Assert.Equal(0, runtime.Releases);
        await Recognition(root).StartRecognitionAsync();
        Assert.Equal(1, runtime.Contexts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootCancellationRetainsEveryConfiguredToken(bool useFirst)
    {
        var runtime = new Runtime();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        await using IWindowsAutomationSession root = Create(runtime);
        await using IWindowsAutomationSession worker = root.Fork();
        root.ConfigurePause(null, first.Token);
        root.ConfigurePause(null, second.Token);
        root.ConfigurePause(null, second.Token);
        await Recognition(worker).StartRecognitionAsync();
        Task<string> result = Recognition(worker).ReadRecognitionAsync(true);
        (useFirst ? first : second).Cancel();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Recognition(worker).StartRecognitionAsync());
        await worker.DisposeAsync();
        await root.DisposeAsync();
        Assert.Equal(5, runtime.Releases);
        Assert.Equal(1, runtime.Disconnects);
    }

    [Fact]
    public async Task UnusedReadStopAndCancellationDoNotCreateComObjects()
    {
        var runtime = new Runtime();
        using var stop = new CancellationTokenSource();
        await using IWindowsAutomationSession root = Create(runtime);
        Assert.Equal(string.Empty, await Recognition(root).ReadRecognitionAsync(false));
        await Recognition(root).StopRecognitionAsync();
        root.ConfigurePause(null, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Recognition(root).StartRecognitionAsync());
        await root.DisposeAsync();
        Assert.Equal(0, runtime.Contexts);
        Assert.Equal(0, runtime.Releases);
    }

    [Fact]
    public async Task RunPreservesCleanupFailures()
    {
        var runtime = new Runtime { FailCleanup = true };
        IWindowsAutomationSession root = Create(runtime);
        await Recognition(root).StartRecognitionAsync();
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            root.DisposeAsync().AsTask());
        Assert.Contains(failure.Flatten().InnerExceptions, error => error.Message == "disconnect");
        Assert.Equal(5, failure.Flatten().InnerExceptions.Count(error => error.Message == "release"));
        Assert.Equal(5, runtime.Releases);
    }

    [Fact]
    public async Task EvaluatesWorkerWordsAfterStoppingExistingGrammar()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime);
        await using IWindowsAutomationSession worker = root.Fork();
        await Recognition(root).StartRecognitionAsync();
        await Recognition(worker).StartRecognitionAsync(async token =>
        {
            Assert.Equal(2, runtime.Stops);
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            return ["登録語"];
        });
        Assert.Contains("<P>登録語</P>", runtime.Grammar);
        Assert.Equal(1, runtime.Contexts);
    }

    [Theory]
    [InlineData(false, "\t語")]
    [InlineData(true, "<#TAB>語")]
    public async Task WordExpressionsReachRunGrammarWithTheirOriginalConversionRules(
        bool preserve, string expected)
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime);
        await using IWindowsAutomationSession worker = root.Fork();
        await Recognition(root).StartRecognitionAsync();
        var context = new WordContext(() =>
        {
            Assert.Equal(2, runtime.Stops);
            return Value.FromArray(UwscArray.Create([1],
                [Value.String("<#TAB>語"), Value.Numeric(123456789012344.5)]));
        });
        await Recognition(worker).StartRecognitionWordsAsync(
            [new NameExpr("words"), new LiteralExpr(Value.String("<#TAB>"))],
            context, preserve);
        Assert.Contains($"<P>{expected}</P>", runtime.Grammar);
        Assert.Contains("<P>123456789012345</P>", runtime.Grammar);
        Assert.EndsWith("<P><#TAB></P>\r\n</L>\r\n</RULE>\r\n</GRAMMAR>\r\n", runtime.Grammar);
        Assert.Equal(1, runtime.Contexts);
    }

    [Fact]
    public async Task WordFunctionCanReenterControlOnTheSameWorkerThread()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime);
        await using IWindowsAutomationSession session = root.Fork();
        await using var worker = new WorkerThreadContext(async token =>
        {
            int thread = Environment.CurrentManagedThreadId;
            var context = new WordContext(() => throw new InvalidOperationException())
            {
                Call = async cancellation =>
                {
                    Assert.Equal(thread, Environment.CurrentManagedThreadId);
                    await Task.Yield();
                    await Recognition(session).StopRecognitionAsync(cancellation);
                    Assert.Equal(thread, Environment.CurrentManagedThreadId);
                    return Value.String("worker語");
                }
            };
            await Recognition(session).StartRecognitionWordsAsync(
                [new CallExpr("word", [])], context, false, token);
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
        });
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("<P>worker語</P>", runtime.Grammar);
    }

    [Fact]
    public async Task UnsupportedWordFailsAfterStoppingAndDoesNotLoadGrammar()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession root = Create(runtime);
        await Recognition(root).StartRecognitionAsync();
        var context = new WordContext(() => Value.FromObject(new object()));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Recognition(root).StartRecognitionWordsAsync([new NameExpr("object")],
                context, false));
        Assert.Equal(2, runtime.Stops);
        Assert.Null(runtime.Grammar);
        await Recognition(root).StartRecognitionAsync();
        Assert.Equal(1, runtime.Contexts);
    }

    private sealed class WordContext(Func<Value> read) : IEvaluationContext
    {
        internal Func<CancellationToken, Task<Value>>? Call;

        public Value Read(string name) => read();

        public async ValueTask<Value> InvokeAsync(CallExpr call, CancellationToken token) =>
            await (Call ?? throw new InvalidOperationException())(token);
    }

    [Fact]
    public async Task PublicCallsReachTheRunGrammarWithoutStartingPhysicalInput()
    {
        var runtime = new Runtime();
        var automation = new WindowsAutomation(_ => throw new InvalidOperationException("物理入力は禁止です。"),
            comRuntime: runtime, swappedMouseButtons: false);
        var lines = new List<string>();
        await new Interpreter(windows: automation).RunAsync(
            "DIM words[]=\"一<#_TAB>語\",\"二\"\nRECOSTATE(TRUE, words, 123456789012344.5)\n"
            + "PRINT DICTATE(FALSE)\nRECOSTATE(FALSE)", lines.Add);
        Assert.Equal([""], lines);
        Assert.Contains("<P>一\t語</P>", runtime.Grammar);
        Assert.Contains("<P>123456789012345</P>", runtime.Grammar);
        Assert.Equal(1, runtime.Contexts);
        Assert.Equal(5, runtime.Releases);
    }

    [Fact]
    public async Task PublicCallsCanStopAndRestartWithDifferentWords()
    {
        var runtime = new Runtime();
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            comRuntime: runtime, swappedMouseButtons: false);
        List<string> lines = [];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter(windows: automation).RunAsync(
            """
            RECOSTATE(TRUE, "最初")
            PRINT DICTATE(FALSE)
            RECOSTATE(FALSE)
            PRINT DICTATE(FALSE)
            RECOSTATE(TRUE, "再開")
            PRINT DICTATE(FALSE)
            RECOSTATE(FALSE)
            """, lines.Add, deadline.Token);

        Assert.Equal(["", "", ""], lines);
        Assert.Contains("<P>再開</P>", runtime.Grammar);
        Assert.DoesNotContain("<P>最初</P>", runtime.Grammar);
        Assert.Equal(1, runtime.Contexts);
        Assert.Equal(1, runtime.Disconnects);
    }

    [Fact]
    public async Task CancelingAReadKeepsTheConnectionUntilSessionDisposal()
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession session = Create(runtime);
        IScriptSpeechRecognitionSession recognition =
            Assert.IsAssignableFrom<IScriptSpeechRecognitionSession>(session);
        using var cancellation = new CancellationTokenSource();
        await recognition.StartRecognitionAsync();
        Task<string> result = recognition.ReadRecognitionAsync(true,
            cancellation.Token);
        Assert.False(result.IsCompleted);
        await cancellation.CancelAsync();
        OperationCanceledException failure =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => result.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(failure.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, runtime.Disconnects);
        Assert.Equal("", await recognition.ReadRecognitionAsync(false));
        await session.DisposeAsync();
        Assert.Equal(1, runtime.Disconnects);
    }

    [Fact]
    public async Task FailedPublicVocabularyEvaluationAllowsRestart()
    {
        var runtime = new Runtime();
        var automation = new WindowsAutomation(_ => { },
            comRuntime: runtime, swappedMouseButtons: false);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            RECOSTATE(TRUE, "最初")
            TRY
                RECOSTATE(TRUE, MissingWord())
            EXCEPT
                PRINT "caught"
            ENDTRY
            PRINT DICTATE(FALSE)
            PRINT VARTYPE(RECOSTATE(TRUE, "再開"))
            """, output.Add);
        Assert.Equal(["caught", "", "258"], output);
        Assert.Contains("<P>再開</P>", runtime.Grammar);
        Assert.DoesNotContain("<P>最初</P>", runtime.Grammar);
        Assert.Equal(1, runtime.Contexts);
        Assert.Equal(1, runtime.Disconnects);
    }

    [Fact]
    public async Task PublicInitializationErrorCanBeCaughtWithoutAnotherErrorDuringExit()
    {
        var runtime = new Runtime
        {
            CreationFailure = ComOperationErrors.Record(new System.Runtime.InteropServices.COMException("作成失敗"))
        };
        var automation = new WindowsAutomation(_ => throw new InvalidOperationException("物理入力は禁止です。"),
            comRuntime: runtime, swappedMouseButtons: false);
        var lines = new List<string>();
        await new Interpreter(windows: automation).RunAsync(
            "TRY\nRECOSTATE(TRUE)\nEXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT \"after\"", lines.Add);
        Assert.Equal(["caught", "after"], lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationFailureIsCachedOnlyForTheCurrentRun(
        bool readBetweenAttempts)
    {
        var runtime = new Runtime
        {
            CreationFailure = ComOperationErrors.Record(
                new System.Runtime.InteropServices.COMException("作成失敗"))
        };
        var automation = new WindowsAutomation(_ => { },
            comRuntime: runtime, swappedMouseButtons: false);
        var interpreter = new Interpreter(windows: automation);
        List<string> output = [];
        string read = readBetweenAttempts ? "DICTATE(FALSE)" : "";
        await interpreter.RunAsync($"""
            TRY
                RECOSTATE(TRUE)
            EXCEPT
                PRINT "first"
            ENDTRY
            TRY
                {read}
                RECOSTATE(TRUE)
            EXCEPT
                PRINT "cached"
            ENDTRY
            """, text =>
            {
                output.Add(text);
                runtime.CreationFailure = null;
            });
        Assert.Equal(["first", "cached"], output);
        Assert.Equal(1, runtime.Contexts);

        await interpreter.RunAsync("RECOSTATE(TRUE)", _ => { });
        Assert.Equal(2, runtime.Contexts);
        Assert.Equal(1, runtime.Disconnects);
    }

    [Theory]
    [InlineData(-0.75, false)]
    [InlineData(0.75, false)]
    [InlineData(1.75, true)]
    [InlineData(4294967296, true)]
    public async Task ScriptFlagsDoNotUse32BitRangeOrNonzeroTruthiness(double number, bool expected)
    {
        var runtime = new Runtime();
        await using IWindowsAutomationSession session = Create(runtime);
        IScriptSpeechRecognitionSession speech = Assert.IsAssignableFrom<IScriptSpeechRecognitionSession>(session);
        Assert.Equal(expected, await speech.ReadRecognitionFlagAsync(Value.Numeric(number), "RECOSTATE", default));
        Assert.Equal(0, runtime.Contexts);
    }

    private static ISpeechRecognitionRunSession Recognition(IWindowsAutomationSession session) =>
        Assert.IsAssignableFrom<ISpeechRecognitionRunSession>(session);

    private static IWindowsAutomationSession Create(Runtime runtime) =>
        new WindowsAutomation(_ => throw new InvalidOperationException("物理入力は禁止です。"),
            comRuntime: runtime, swappedMouseButtons: false).CreateSession();

    private sealed class Runtime : IComRuntime
    {
        internal int Contexts;
        internal int Releases;
        internal int Disconnects;
        internal int Stops;
        internal bool FailCleanup;
        internal string? Grammar;
        internal Exception? CreationFailure;

        public object Create(string name)
        {
            if (name == "SAPI.SpInProcRecoContext")
            {
                Interlocked.Increment(ref Contexts);
                if (CreationFailure is not null)
                {
                    throw CreationFailure;
                }
            }

            return new object();
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            switch (name)
            {
                case "Default":
                    return "fake-input";
                case "Recognizer":
                case "CreateGrammar":
                    return new object();
                case "CmdSetRuleIdState":
                case "DictationSetState":
                    if (Equals(Assert.IsType<ComVariantValue>(arguments[^1]).Value, 0))
                    {
                        Interlocked.Increment(ref Stops);
                    }

                    return null;
                case "CmdLoadFromFile":
                    Grammar = File.ReadAllText(Assert.IsType<string>(arguments[0]));
                    return null;
                case "SetId":
                case "AudioInput":
                    return null;
                default:
                    throw new InvalidOperationException(name);
            }
        }

        public IComEventConnection ConnectEvent(object target, string interfaceName,
            string eventName, NativeComEventHandler callback, CancellationToken token) =>
            new Connection(this);

        public void Release(object target)
        {
            Interlocked.Increment(ref Releases);
            if (FailCleanup)
            {
                throw new InvalidDataException("release");
            }
        }

        private sealed class Connection(Runtime runtime) : IComEventConnection
        {
            public Exception? TakeCallbackFailure() => null;

            public void Dispose()
            {
                Interlocked.Increment(ref runtime.Disconnects);
                if (runtime.FailCleanup)
                {
                    throw new InvalidOperationException("disconnect");
                }
            }
        }
    }
}
