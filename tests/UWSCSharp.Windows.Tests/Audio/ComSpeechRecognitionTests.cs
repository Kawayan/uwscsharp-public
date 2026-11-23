using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 偽COMとAutomation値変換を使い、認識器・マイク・画面を使用しません。
[SupportedOSPlatform("windows")]
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ComSpeechRecognitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguresDefaultInputAndDynamicGrammarOnOneSta(bool typed)
    {
        var runtime = new Runtime { TypedResults = typed };
        await using ComSpeechRecognition recognition =
            await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        await recognition.StartGrammarFileAsync("日本語.xml", beforeStart: () =>
            Assert.Equal("connect", runtime.Calls.Last().Name));
        await recognition.StartDictationAsync(beforeStart: () =>
            Assert.Equal("grammar.DictationSetState", runtime.Calls.Last().Name));
        await recognition.StopAsync();
        await recognition.DisposeAsync();
        Assert.Equal([
            "create:SAPI.SpInProcRecoContext", "create:SAPI.SpObjectTokenCategory",
            "category.SetId", "create:SAPI.SpObjectToken", "category.Default",
            "input.SetId", "context.Recognizer", "recognizer.AudioInput",
            "context.CreateGrammar", "connect", "grammar.CmdLoadFromFile",
            "grammar.CmdSetRuleIdState", "grammar.CmdSetRuleIdState",
            "grammar.DictationSetState", "grammar.DictationSetState",
            "grammar.CmdSetRuleIdState", "grammar.DictationSetState",
            "grammar.CmdSetRuleIdState", "grammar.DictationSetState", "disconnect",
            "release:grammar", "release:recognizer", "release:input",
            "release:category", "release:context"
        ], runtime.Calls.Select(call => call.Name));
        Assert.Single(runtime.Calls.Select(call => call.Thread).Distinct());
        Assert.All(runtime.Calls, call => Assert.Equal(ApartmentState.STA, call.Apartment));
        Assert.Equal(5, runtime.Released.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => recognition.StartDictationAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task CreationFailureReleasesEveryAcquiredObject(int failAt)
    {
        var runtime = new Runtime { FailAt = failAt };
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ComSpeechRecognition.CreateAsync(_ => { }, runtime));
        Assert.Same(runtime.Failure, failure);
        Assert.Equal(runtime.Acquired.AsEnumerable().Reverse(), runtime.Released);
    }

    [Fact]
    public async Task InvalidDefaultIdIsRejectedAndItsReferenceReleased()
    {
        var runtime = new Runtime { InvalidDefault = true };
        SpeechRecognitionInitializationException failure =
            await Assert.ThrowsAsync<SpeechRecognitionInitializationException>(() =>
                ComSpeechRecognition.CreateAsync(_ => { }, runtime));
        Assert.IsType<InvalidDataException>(failure.InnerException);
        Assert.Equal(runtime.Acquired.AsEnumerable().Reverse(), runtime.Released);
        Assert.Contains("invalid-default", runtime.Released);
    }

    [Fact]
    public async Task ScriptHandlesInvalidDefaultInputAsInitializationFailure()
    {
        var runtime = new Runtime { InvalidDefault = true };
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: runtime)).RunAsync("""
                TRY
                    RECOSTATE(TRUE)
                EXCEPT
                    PRINT TRY_ERRMSG
                ENDTRY
                TRY
                    DICTATE(FALSE)
                EXCEPT
                    PRINT TRY_ERRMSG
                ENDTRY
                PRINT "continued"
                """, output.Add);

        Assert.Equal(3, output.Count);
        Assert.Contains("SAPIが存在しないか or 初期化エラー", output[0]);
        Assert.Contains("SAPIが存在しないか or 初期化エラー", output[1]);
        Assert.Equal("continued", output[2]);
        Assert.Equal(0, runtime.Activations);
        Assert.Equal(runtime.Acquired.AsEnumerable().Reverse(), runtime.Released);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("category")]
    [InlineData("input")]
    [InlineData("recognizer")]
    [InlineData("grammar")]
    public async Task ScriptHandlesInvalidRecognitionObjectAsInitializationFailure(
        string invalidObject)
    {
        var runtime = new Runtime { InvalidObject = invalidObject };
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: runtime)).RunAsync("""
                TRY
                    RECOSTATE(TRUE)
                EXCEPT
                    PRINT TRY_ERRMSG
                ENDTRY
                PRINT "continued"
                """, output.Add);

        Assert.Equal(2, output.Count);
        Assert.Contains("SAPIが存在しないか or 初期化エラー", output[0]);
        Assert.Equal("continued", output[1]);
        Assert.Equal(0, runtime.Activations);
        Assert.Equal(runtime.Acquired.AsEnumerable().Reverse(), runtime.Released);
    }

    [Fact]
    public async Task FailedLoadDoesNotActivateAndAllowsAnotherRequest()
    {
        var runtime = new Runtime();
        await using ComSpeechRecognition recognition =
            await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        runtime.BeforeInvoke = name =>
        {
            if (name == "CmdLoadFromFile")
            {
                throw runtime.Failure;
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recognition.StartGrammarFileAsync("日本語.xml"));
        Assert.Equal(0, runtime.Activations);
        runtime.BeforeInvoke = null;
        await recognition.StartDictationAsync();
        Assert.Equal(1, runtime.Activations);
    }

    [Fact]
    public async Task CancellationDuringLoadWaitsForComAndDisposalReleasesAfterward()
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime();
        ComSpeechRecognition recognition = await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        runtime.BeforeInvoke = name =>
        {
            if (name == "CmdLoadFromFile")
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        Task starting = recognition.StartGrammarFileAsync("日本語.xml", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Task disposing = recognition.DisposeAsync().AsTask();
            Assert.False(starting.IsCompleted);
            Assert.False(disposing.IsCompleted);
            Assert.Empty(runtime.Released);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, runtime.Activations);
            Assert.Equal(5, runtime.Released.Count);
        }
        finally
        {
            release.Set();
            await recognition.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShutdownAttemptsBothStopsDisconnectAndEveryRelease()
    {
        var runtime = new Runtime();
        ComSpeechRecognition recognition = await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        runtime.BeforeInvoke = _ => throw runtime.Failure;
        runtime.CleanupFails = true;
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            recognition.DisposeAsync().AsTask());
        Assert.Equal(8, failure.Flatten().InnerExceptions.Count);
        Assert.Equal(5, runtime.Released.Count);
        Assert.Contains(runtime.Calls, call => call.Name == "disconnect");
        Task repeated = recognition.DisposeAsync().AsTask();
        Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => repeated));
    }

    [Fact]
    public async Task ExposesNotificationFailureAndRejectsInvalidArgumentsBeforeCom()
    {
        var runtime = new Runtime();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ComSpeechRecognition.CreateAsync(null!, runtime));
        Assert.Empty(runtime.Calls);
        await using ComSpeechRecognition recognition =
            await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        Assert.Throws<ArgumentException>(() =>
        {
            _ = recognition.StartGrammarFileAsync(string.Empty);
        });
        runtime.CallbackFailure = runtime.Failure;
        Assert.Same(runtime.Failure, recognition.TakeCallbackFailure());
        Assert.Null(recognition.TakeCallbackFailure());
    }

    [Fact]
    public async Task ConcurrentStartsDoNotInterleaveGrammarReplacement()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new Runtime();
        await using ComSpeechRecognition recognition =
            await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        runtime.BeforeInvoke = name =>
        {
            if (name == "CmdLoadFromFile")
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        Task first = recognition.StartGrammarFileAsync("日本語.xml");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task second = recognition.StartDictationAsync();
            Assert.False(second.IsCompleted);
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal([
                "grammar.CmdLoadFromFile", "grammar.CmdSetRuleIdState",
                "grammar.CmdSetRuleIdState", "grammar.DictationSetState",
                "grammar.DictationSetState"
            ], runtime.Calls.Select(call => call.Name).Where(name => name.StartsWith("grammar.")));
            Assert.Equal(2, runtime.Activations);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CancellationAfterConnectionDisconnectsAndReleases()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime { AfterConnect = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ComSpeechRecognition.CreateAsync(_ => { }, runtime, cancellation.Token));
        Assert.Equal(runtime.Acquired.AsEnumerable().Reverse(), runtime.Released);
        Assert.Equal(5, runtime.Released.Count);
        Assert.Contains(runtime.Calls, call => call.Name == "disconnect");
    }

    [Fact]
    public async Task CreationAndCleanupFailuresAreBothRetained()
    {
        var runtime = new Runtime { FailAt = 10, CleanupFails = true };
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            ComSpeechRecognition.CreateAsync(_ => { }, runtime));
        Assert.Equal(6, failure.Flatten().InnerExceptions.Count);
        Assert.Equal(5, runtime.Released.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task GeneratedGrammarIsOwnedThroughLoadAndActivation(int outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new Runtime();
        await using var owner = new SpeechRecognitionOwner(runtime);
        await owner.StartDictationAsync();
        string? savedPath = null;
        runtime.LoadFile = path =>
        {
            savedPath = path;
            Assert.True(File.Exists(path));
            Assert.Contains("<P>登録語</P>\r\n", File.ReadAllText(path));
            Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, File.ReadAllBytes(path)[..3]);
            if (outcome == 1)
            {
                throw runtime.Failure;
            }

            if (outcome == 3)
            {
                cancellation.Cancel();
            }
        };
        runtime.BeforeInvoke = name =>
        {
            if (name == "CmdSetRuleIdState" && savedPath is not null)
            {
                Assert.True(File.Exists(savedPath));
                if (outcome == 2)
                {
                    throw runtime.Failure;
                }
            }
        };
        IEnumerable<string> Words()
        {
            Assert.Equal("grammar.DictationSetState", runtime.Calls.Last().Name);
            Assert.Equal(string.Empty, owner.TakeLatest());
            yield return "登録語";
        }

        try
        {
            Task starting = owner.StartWordsAsync(Words(), cancellation.Token);
            if (outcome == 0)
            {
                await starting;
            }
            else if (outcome == 3)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            }
            else
            {
                Assert.Same(runtime.Failure,
                    await Assert.ThrowsAsync<InvalidOperationException>(() => starting));
            }

            Assert.NotNull(savedPath);
            Assert.False(File.Exists(savedPath));
            Assert.Equal(outcome == 0 ? 2 : 1, runtime.Activations);
        }
        finally
        {
            runtime.BeforeInvoke = null;
        }
    }

    [Fact]
    public async Task WordEnumerationFailureOccursAfterOldGrammarStops()
    {
        var runtime = new Runtime();
        await using ComSpeechRecognition recognition =
            await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        await recognition.StartDictationAsync();
        bool cleared = false;
        IEnumerable<string> Words()
        {
            Assert.True(cleared);
            Assert.Equal("grammar.DictationSetState", runtime.Calls.Last().Name);
            yield return "登録語";
            throw runtime.Failure;
        }

        Assert.Same(runtime.Failure,
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recognition.StartWordsAsync(Words(), beforeStart: () => cleared = true)));
        Assert.DoesNotContain(runtime.Calls, call => call.Name == "grammar.CmdLoadFromFile");
        Assert.Equal(1, runtime.Activations);
        await recognition.StartDictationAsync();
        Assert.Equal(2, runtime.Activations);
    }

    [Fact]
    public async Task EvaluatedValuesReachTheGrammarAfterStoppingOldRecognition()
    {
        var runtime = new Runtime();
        await using var owner = new SpeechRecognitionOwner(runtime);
        await owner.StartDictationAsync();
        var formatter = new SpeechRecognitionWordFormatter();
        var context = new WordContext(() =>
        {
            Assert.Equal("grammar.DictationSetState", runtime.Calls.Last().Name);
            return Value.FromArray(UwscArray.Create([3],
                [Value.String("<#_TAB>語"), Value.Numeric(123456789012344.5),
                    Value.Logical(true), new Value(ValueKind.Null)]));
        });
        string? savedPath = null;
        runtime.LoadFile = path =>
        {
            savedPath = path;
            string xml = File.ReadAllText(path);
            Assert.Contains("<P>\t語</P>\r\n", xml);
            Assert.Contains("<P>123456789012345</P>\r\n", xml);
            Assert.Contains("<P>True</P>\r\n", xml);
            Assert.Contains("<P>NULL</P>\r\n", xml);
            Assert.Contains("<P><#_TAB></P>\r\n", xml);
            Assert.Equal(1, runtime.Activations);
        };
        await owner.StartEvaluatedWordsAsync(token =>
            SpeechRecognitionWordEvaluation.EvaluateAsync(
                [new NameExpr("words"), new LiteralExpr(Value.String("<#_TAB>"))],
                context, formatter.FormatAsync, false, token));
        Assert.NotNull(savedPath);
        Assert.False(File.Exists(savedPath));
        Assert.Equal(2, runtime.Activations);
    }

    private sealed class WordContext(Func<Value> read) : IEvaluationContext
    {
        public Value Read(string name) => read();

        public ValueTask<Value> InvokeAsync(CallExpr call, CancellationToken token) =>
            throw new InvalidOperationException("この検証では関数を呼びません。");
    }

    [Fact]
    public async Task AsyncWordsKeepWorkerContextAndAllowNestedControl()
    {
        var runtime = new Runtime();
        string? path = null;
        runtime.LoadFile = value => path = value;
        await using var owner = new SpeechRecognitionOwner(runtime);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new WorkerThreadContext(async token =>
        {
            int thread = Environment.CurrentManagedThreadId;
            await owner.StartDictationAsync(token);
            await owner.StartEvaluatedWordsAsync(async cancellation =>
            {
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                Assert.Equal("grammar.DictationSetState", runtime.Calls.Last().Name);
                Assert.Equal(string.Empty, owner.TakeLatest());
                entered.TrySetResult();
                await resume.Task.WaitAsync(cancellation);
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                await owner.StopAsync(cancellation);
                await owner.StartDictationAsync(cancellation);
                Assert.Equal(thread, Environment.CurrentManagedThreadId);
                return ["非同期語彙"];
            }, token);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task outside = owner.StopAsync();
            Assert.False(outside.IsCompleted);
            resume.TrySetResult();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await outside.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(path);
            Assert.False(File.Exists(path));
            Assert.Equal(3, runtime.Activations);
        }
        finally
        {
            resume.TrySetResult();
            await worker.RequestCancellationAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncWordsCancellationWaitsForEvaluationWithoutLoading(bool shutdown)
    {
        var runtime = new Runtime();
        await using var owner = new SpeechRecognitionOwner(runtime);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken evaluating = default;
        Task starting = owner.StartEvaluatedWordsAsync(async token =>
        {
            evaluating = token;
            entered.TrySetResult();
            await resume.Task;
            return ["取消後"];
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task ending = shutdown ? owner.DisposeAsync().AsTask() : Task.CompletedTask;
            if (!shutdown)
            {
                cancellation.Cancel();
            }

            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = evaluating.Register(() =>
                cancelled.TrySetResult());
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(evaluating.IsCancellationRequested);
            Assert.False(starting.IsCompleted);
            if (shutdown)
            {
                Assert.False(ending.IsCompleted);
                Assert.Empty(runtime.Released);
            }

            resume.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            await ending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(runtime.Calls, call => call.Name == "grammar.CmdLoadFromFile");
            Assert.Equal(0, runtime.Activations);
        }
        finally
        {
            resume.TrySetResult();
        }
    }

    [Fact]
    public async Task AsyncWordFailureAllowsNextRequest()
    {
        var runtime = new Runtime();
        await using var owner = new SpeechRecognitionOwner(runtime);
        await owner.StartDictationAsync();
        Assert.Same(runtime.Failure,
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                owner.StartEvaluatedWordsAsync(async token =>
                {
                    await Task.Yield();
                    throw runtime.Failure;
                })));
        Assert.DoesNotContain(runtime.Calls, call => call.Name == "grammar.CmdLoadFromFile");
        await owner.StartDictationAsync();
        Assert.Equal(2, runtime.Activations);
    }

    private sealed record Instance(string Name);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task ClassifiedInitializationFailureKeepsNativeCauseAndReleasesReferences(int failAt)
    {
        COMException cause = ComOperationErrors.Record(
            new COMException("native initialization", unchecked((int)0x80040154)));
        var runtime = new Runtime { FailAt = failAt, ClassifiedFailure = cause };
        SpeechRecognitionInitializationException failure =
            await Assert.ThrowsAsync<SpeechRecognitionInitializationException>(() =>
                ComSpeechRecognition.CreateAsync(_ => { }, runtime));
        Assert.Equal("SAPIが存在しないか or 初期化エラー", failure.Message);
        Assert.Same(cause, failure.InnerException);
        Assert.Equal(cause.HResult, failure.HResult);
        Assert.Equal(runtime.Acquired.AsEnumerable().Reverse(), runtime.Released);
    }

    [Fact]
    public async Task ClassifiedInitializationAndCleanupFailuresAreBothRetained()
    {
        COMException cause = ComOperationErrors.Record(new COMException("initialization"));
        var runtime = new Runtime { FailAt = 10, ClassifiedFailure = cause, CleanupFails = true };
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            ComSpeechRecognition.CreateAsync(_ => { }, runtime));
        IReadOnlyCollection<Exception> errors = failure.Flatten().InnerExceptions;
        SpeechRecognitionInitializationException initialization =
            Assert.Single(errors.OfType<SpeechRecognitionInitializationException>());
        Assert.Same(cause, initialization.InnerException);
        Assert.Equal(5, errors.Count(error => ReferenceEquals(error, runtime.Failure)));
        Assert.Equal(5, runtime.Released.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnclassifiedAndMemoryInitializationFailuresKeepTheirIdentity(int kind)
    {
        Exception cause = kind switch
        {
            0 => new COMException("未分類のCOM例外"),
            1 => ComOperationErrors.Record(new OutOfMemoryException("メモリ不足")),
            _ => ComOperationErrors.Record(new COMException("native memory", unchecked((int)0x8007000e)))
        };
        var runtime = new Runtime { FailAt = 1, ClassifiedFailure = cause };
        Exception observed = await Record.ExceptionAsync(() =>
            ComSpeechRecognition.CreateAsync(_ => { }, runtime));
        Assert.Same(cause, observed);
        Assert.Empty(runtime.Acquired);
    }

    [Theory]
    [InlineData("CmdSetRuleIdState")]
    [InlineData("DictationSetState")]
    public async Task KnownComStopFailureAllowsWordEvaluationAndRestart(string method)
    {
        var runtime = new Runtime();
        await using ComSpeechRecognition recognition =
            await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        await recognition.StartDictationAsync();
        COMException failure = ComOperationErrors.Record(new COMException("停止失敗"));
        bool failed = false;
        runtime.BeforeInvoke = name =>
        {
            if (name == method && !failed)
            {
                failed = true;
                throw failure;
            }
        };
        runtime.LoadFile = _ => { };
        bool evaluated = false;
        await recognition.StartEvaluatedWordsAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            Assert.True(failed);
            evaluated = true;
            return Task.FromResult<IEnumerable<string>>(["再開語"]);
        });
        Assert.True(evaluated);
        Assert.Equal(2, runtime.Activations);
    }

    [Fact]
    public async Task ExplicitStopConsumesKnownComFailureButDisposalRetainsIt()
    {
        var runtime = new Runtime();
        ComSpeechRecognition recognition =
            await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        await recognition.StartDictationAsync();
        COMException failure = ComOperationErrors.Record(new COMException("停止失敗"));
        runtime.BeforeInvoke = name =>
        {
            if (name == "CmdSetRuleIdState")
            {
                throw failure;
            }
        };
        await recognition.StopAsync();
        Assert.Equal("grammar.CmdSetRuleIdState", runtime.Calls.Last().Name);
        AggregateException cleanup = await Assert.ThrowsAsync<AggregateException>(() =>
            recognition.DisposeAsync().AsTask());
        Assert.Contains(failure, cleanup.Flatten().InnerExceptions);
        Assert.Equal(5, runtime.Released.Count);
        Assert.Contains(runtime.Calls, call => call.Name == "grammar.DictationSetState");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnclassifiedAndMemoryFailuresDoNotReachWordEvaluation(int kind)
    {
        var runtime = new Runtime();
        await using ComSpeechRecognition recognition =
            await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        await recognition.StartDictationAsync();
        Exception failure = kind switch
        {
            0 => new InvalidOperationException("未知の障害"),
            1 => new COMException("未分類のCOM例外"),
            _ => ComOperationErrors.Record(new OutOfMemoryException("メモリ不足"))
        };
        runtime.BeforeInvoke = _ => throw failure;
        bool evaluated = false;
        try
        {
            Exception observed = await Record.ExceptionAsync(() =>
                recognition.StartEvaluatedWordsAsync(_ =>
                {
                    evaluated = true;
                    return Task.FromResult<IEnumerable<string>>(["実行しない語"]);
                }));
            Assert.Same(failure, observed);
            Assert.False(evaluated);
            Assert.Equal(1, runtime.Activations);
        }
        finally
        {
            runtime.BeforeInvoke = null;
        }
    }

    [Fact]
    public async Task CancellationDuringKnownStopFailurePreventsWordEvaluation()
    {
        var runtime = new Runtime();
        using var cancellation = new CancellationTokenSource();
        await using ComSpeechRecognition recognition =
            await ComSpeechRecognition.CreateAsync(_ => { }, runtime);
        await recognition.StartDictationAsync();
        runtime.BeforeInvoke = _ =>
        {
            cancellation.Cancel();
            throw ComOperationErrors.Record(new COMException("停止中に取消"));
        };
        bool evaluated = false;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                recognition.StartEvaluatedWordsAsync(_ =>
                {
                    evaluated = true;
                    return Task.FromResult<IEnumerable<string>>(["実行しない語"]);
                }, cancellation.Token));
            Assert.False(evaluated);
            Assert.Equal(1, runtime.Activations);
        }
        finally
        {
            runtime.BeforeInvoke = null;
        }
    }

    private sealed class Runtime : IComRuntime
    {
        internal readonly ConcurrentQueue<(string Name, int Thread, ApartmentState Apartment)> Calls = new();
        internal readonly List<string> Acquired = [];
        internal readonly List<string> Released = [];
        internal readonly InvalidOperationException Failure = new("認識COM失敗");
        internal Exception? ClassifiedFailure;
        internal int FailAt;
        internal int Activations;
        internal bool InvalidDefault;
        internal string? InvalidObject;
        internal bool TypedResults;
        internal bool CleanupFails;
        internal Action<string>? BeforeInvoke;
        internal Action<string>? LoadFile;
        internal Action? AfterConnect;
        internal Exception? CallbackFailure;
        private int steps;

        private void Record(string name, bool canFail = true)
        {
            Calls.Enqueue((name, Environment.CurrentManagedThreadId,
                Thread.CurrentThread.GetApartmentState()));
            if (canFail && ++steps == FailAt)
            {
                throw ClassifiedFailure ?? Failure;
            }
        }

        private object Acquire(string name)
        {
            Acquired.Add(name);
            return new Instance(name);
        }

        public object Create(string name)
        {
            Record("create:" + name);
            string kind = name switch
            {
                "SAPI.SpInProcRecoContext" => "context",
                "SAPI.SpObjectTokenCategory" => "category",
                "SAPI.SpObjectToken" => "input",
                _ => throw new InvalidOperationException(name)
            };
            return kind == InvalidObject ? 0 : Acquire(kind);
        }

        public object GetActive(string name) => throw new NotSupportedException();

        public object? Invoke(object target, string name, object?[] arguments, bool assign)
        {
            string owner = Assert.IsType<Instance>(target).Name;
            Record(owner + "." + name);
            BeforeInvoke?.Invoke(name);
            Assert.Equal(name == "AudioInput", assign);
            switch (name)
            {
                case "SetId":
                    Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\AudioInput",
                        arguments[owner == "category" ? 0 : 1]);
                    ComVariantValue flag = Assert.IsType<ComVariantValue>(arguments[^1]);
                    Assert.Equal((ushort)11, flag.Type);
                    Assert.Equal(false, flag.Value);
                    if (owner == "input")
                    {
                        Assert.Equal("default-input", Unwrap(arguments[0]));
                    }

                    break;
                case "Default":
                    return InvalidDefault ? Acquire("invalid-default")
                        : TypedResults ? new ComVariantValue(8, "default-input") : "default-input";
                case "Recognizer":
                    return ReferenceResult("recognizer");
                case "AudioInput":
                    Assert.Equal("input", Assert.IsType<Instance>(Unwrap(arguments.Single())).Name);
                    break;
                case "CreateGrammar":
                    Assert.Equal(0, Integer(arguments.Single()));
                    return ReferenceResult("grammar");
                case "CmdLoadFromFile":
                    if (LoadFile is null)
                    {
                        Assert.Equal("日本語.xml", arguments[0]);
                    }
                    else
                    {
                        LoadFile(Assert.IsType<string>(arguments[0]));
                    }

                    Assert.Equal(1, Integer(arguments[1]));
                    break;
                case "CmdSetRuleIdState":
                    Assert.Equal(0, Integer(arguments[0]));
                    Activations += Integer(arguments[1]);
                    break;
                case "DictationSetState":
                    Activations += Integer(arguments.Single());
                    break;
                default:
                    throw new InvalidOperationException(name);
            }

            return null;
        }

        private object ReferenceResult(string name)
        {
            if (name == InvalidObject)
            {
                return 0;
            }

            object reference = Acquire(name);
            return TypedResults
                ? new ComVariantValue(9, reference) { Acquisitions = [reference] }
                : reference;
        }

        private static object? Unwrap(object? value) =>
            value is ComVariantValue typed ? typed.Value : value;

        private static int Integer(object? argument)
        {
            ComVariantValue value = Assert.IsType<ComVariantValue>(argument);
            Assert.Equal((ushort)3, value.Type);
            return Assert.IsType<int>(value.Value);
        }

        public IComEventConnection ConnectEvent(object target, string interfaceName,
            string eventName, NativeComEventHandler callback, CancellationToken token)
        {
            Record("connect");
            Assert.Equal("context", Assert.IsType<Instance>(target).Name);
            Assert.Equal("_ISpeechRecoContextEvents", interfaceName);
            Assert.Equal("Recognition", eventName);
            AfterConnect?.Invoke();
            return new Connection(this);
        }

        public void Release(object target)
        {
            string name = Assert.IsType<Instance>(target).Name;
            Record("release:" + name, false);
            Released.Add(name);
            if (CleanupFails)
            {
                throw Failure;
            }
        }

        private sealed class Connection(Runtime owner) : IComEventConnection
        {
            public Exception? TakeCallbackFailure() =>
                Interlocked.Exchange(ref owner.CallbackFailure, null);

            public void Dispose()
            {
                owner.Record("disconnect", false);
                if (owner.CleanupFails)
                {
                    throw owner.Failure;
                }
            }
        }
    }
}
