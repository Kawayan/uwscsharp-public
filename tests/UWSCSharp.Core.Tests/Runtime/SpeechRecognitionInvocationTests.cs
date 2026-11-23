using System.Collections.Concurrent;
using System.Globalization;
using Xunit;

namespace UWSCSharp.Core.Tests;

// 実行先は管理された偽物だけを使い、音声・画面・入力を操作しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionInvocationTests
{
    [Fact]
    public async Task StopSkipsWordExpressionsAndReturnsEmptyString()
    {
        var automation = new Automation();
        await Run("RECOSTATE(FALSE, undefined())\nPRINT RECOSTATE()", automation);
        Assert.Equal(["stop", "stop", "out:"], automation.Trace);
    }

    [Fact]
    public async Task StartEvaluatesWordsAfterStopAndExpandsOnlyInternalArrayMarkers()
    {
        var automation = new Automation();
        await Run("""
            DIM words[] = "一<#_TAB>語", "二"
            RECOSTATE(Flag(), Word(), words)
            FUNCTION Flag()
                PRINT "flag"
                RESULT = TRUE
            FEND
            FUNCTION Word()
                PRINT "word"
                RESULT = "<#_TAB>"
            FEND
            """, automation);
        Assert.Equal(["out:flag", "stop", "out:word", "start:<#_TAB>|一\t語|二"], automation.Trace);
    }

    [Theory]
    [InlineData("DICTATE()", true)]
    [InlineData("DICTATE(FALSE)", false)]
    [InlineData("DICTATE(-1)", true)]
    [InlineData("DICTATE(0.75)", false)]
    public async Task DictateDefaultAndExplicitFlagsSelectReadMode(string call, bool wait)
    {
        var automation = new Automation();
        automation.Result.TrySetResult("結果");
        await Run("PRINT " + call, automation);
        Assert.Equal([wait], automation.ReadModes);
        Assert.Equal([wait ? "out:結果" : "out:"], automation.Trace);
    }

    [Fact]
    public async Task AdditionalDictateArgumentsAreEvaluatedButDoNotChangeTheFirstFlag()
    {
        var automation = new Automation();
        await Run("""
            PRINT DICTATE(FALSE, Extra())
            FUNCTION Extra()
                PRINT "extra"
                RESULT = TRUE
            FEND
            """, automation);
        Assert.Equal([false], automation.ReadModes);
        Assert.Equal(["out:extra", "out:"], automation.Trace);
    }

    [Theory]
    [InlineData("RECOSTATE")]
    [InlineData("DICTATE")]
    public async Task InvalidCallShapeIsRejectedBeforeSideEffects(string function)
    {
        string source = function + "(Side()," + string.Join(",", Enumerable.Repeat("0", 37))
            + ")\nFUNCTION Side()\nPRINT \"side\"\nRESULT=1\nFEND";
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() => Run(source, automation));
        Assert.Empty(automation.Trace);
        ScriptCallAnalysis analysis = ScriptCallAnalyzer.Analyze(source);
        Assert.Single(analysis.Diagnostics);
    }

    [Theory]
    [InlineData("RECOSTATE(,1)")]
    [InlineData("RECOSTATE(TRUE,,1)")]
    [InlineData("DICTATE(,1)")]
    public async Task OmittedPositionsAreDiagnosed(string source)
    {
        var automation = new Automation();
        await Assert.ThrowsAsync<ScriptException>(() => Run(source, automation));
        Assert.Empty(automation.Trace);
        Assert.Single(ScriptCallAnalyzer.Analyze(source).Diagnostics);
    }

    [Fact]
    public async Task WordExpressionFailureRemainsCatchableAndDoesNotStart()
    {
        var automation = new Automation();
        await Run("TRY\nRECOSTATE(TRUE, undefined())\nEXCEPT\nPRINT \"caught\"\nENDTRY", automation);
        Assert.Equal(["stop", "out:caught"], automation.Trace);
    }

    [Fact]
    public async Task UnknownBackendFailureIsNotCaughtByScript()
    {
        var failure = new IOException("host failure");
        var automation = new Automation { Failure = failure };
        IOException observed = await Assert.ThrowsAsync<IOException>(() => Run(
            "TRY\nRECOSTATE(TRUE)\nEXCEPT\nPRINT \"caught\"\nENDTRY", automation));
        Assert.Same(failure, observed);
        Assert.Empty(automation.Trace);
    }

    [Fact]
    public async Task CancellationUnblocksDictateAndDoesNotExecuteNextStatement()
    {
        var automation = new Automation();
        using var cancellation = new CancellationTokenSource();
        Task run = new Interpreter(windows: automation).RunAsync(
            "PRINT DICTATE()\nPRINT \"after\"", text => automation.Trace.Enqueue("out:" + text), cancellation.Token);
        await automation.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(automation.Trace);
    }

    [Fact]
    public async Task WorkerWordsCanCompleteAParentWaitAndKeepTheWorkerContext()
    {
        var automation = new Automation();
        await Run("""
            THREAD Recognize()
            PRINT DICTATE()
            PROCEDURE Recognize()
                RECOSTATE(TRUE, Word())
            FEND
            FUNCTION Word()
                SLEEP(0.01)
                RESULT = "worker"
            FEND
            """, automation).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("out:worker", automation.Trace);
        Assert.Equal(1, automation.Forks);
        Assert.NotEqual(0, automation.StartThread);
        Assert.Equal(automation.StartThread, automation.FormatThread);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("-0.99", false)]
    [InlineData("1.99", true)]
    [InlineData("4294967296", true)]
    [InlineData("-9223372036854775808", true)]
    [InlineData("$FFFFFFFF", true)]
    [InlineData("1e+2", true)]
    public void FlagsUseSigned64BitTruncation(string text, bool expected) =>
        Assert.Equal(expected, SpeechRecognitionFlag.Read(text));

    [Theory]
    [InlineData("")]
    [InlineData("TRUE")]
    [InlineData("1e2")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854777856")]
    public void InvalidFlagsAreNotTruthinessConversions(string text) =>
        Assert.Throws<InvalidOperationException>(() => SpeechRecognitionFlag.Read(text));

    private static Task Run(string source, Automation automation) =>
        new Interpreter(windows: automation).RunAsync(source,
            text => automation.Trace.Enqueue("out:" + text));

    private sealed class Automation : IWindowsAutomation
    {
        internal readonly ConcurrentQueue<string> Trace = new();
        internal readonly ConcurrentQueue<bool> ReadModes = new();
        internal readonly TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource WaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Failure;
        internal Exception? ValueFailure;
        internal int Forks;
        internal int StartThread;
        internal int FormatThread;

        public IWindowsAutomationSession CreateSession() => new Session(this);

        private sealed class Session(Automation owner) : IWindowsAutomationSession, IScriptSpeechRecognitionSession
        {
            public IWindowsAutomationSession Fork()
            {
                Interlocked.Increment(ref owner.Forks);
                return new Session(owner);
            }

            public ValueTask<bool> ReadRecognitionFlagAsync(Value value, string function, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string text = value.Kind == ValueKind.String ? value.Text
                        : value.AsNumber().ToString("R", CultureInfo.InvariantCulture);
                    return ValueTask.FromResult(SpeechRecognitionFlag.Read(text));
                }
                catch (InvalidOperationException failure)
                {
                    owner.ValueFailure = failure;
                    throw;
                }
            }

            public ValueTask<string> FormatRecognitionWordAsync(Value value, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                owner.FormatThread = Environment.CurrentManagedThreadId;
                return ValueTask.FromResult(value.Display());
            }

            public async Task StartRecognitionAsync(Func<CancellationToken, Task<IEnumerable<string>>>? evaluate = null,
                CancellationToken token = default)
            {
                if (owner.Failure is not null)
                {
                    throw owner.Failure;
                }

                owner.StartThread = Environment.CurrentManagedThreadId;
                owner.Trace.Enqueue("stop");
                string[] words = evaluate is null ? []
                    : [.. await evaluate(token).ConfigureAwait(WorkerThreadContext.IsCurrent)];
                owner.Trace.Enqueue("start:" + string.Join("|", words));
                if (words.Length > 0)
                {
                    owner.Result.TrySetResult(words[0]);
                }
            }

            public Task StopRecognitionAsync(CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                owner.Trace.Enqueue("stop");
                return Task.CompletedTask;
            }

            public async Task<string> ReadRecognitionAsync(bool wait, CancellationToken token = default)
            {
                owner.ReadModes.Enqueue(wait);
                owner.WaitEntered.TrySetResult();
                return wait ? await owner.Result.Task.WaitAsync(token) : "";
            }

            public string? GetScriptErrorMessage(string name, Exception failure) =>
                ReferenceEquals(failure, owner.ValueFailure) ? failure.Message : null;

            public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments, CancellationToken token) =>
                throw new InvalidOperationException("汎用の先行評価へ流さないでください。");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
