using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class OptFinallyCompletionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-opt-finally-" + Guid.NewGuid());

    public OptFinallyCompletionTests()
    {
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        // このテストが所有するフォルダーだけを削除します。
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData("body", false)]
    [InlineData("body", true)]
    [InlineData("nested", false)]
    [InlineData("nested", true)]
    [InlineData("active-finally", false)]
    [InlineData("active-finally", true)]
    [InlineData("child", false)]
    [InlineData("child", true)]
    [InlineData("unused-child", false)]
    [InlineData("unused-child", true)]
    public async Task CancellationKeepsScopeOrderAndCompilationWidePolicy(
        string variant, bool enabled)
    {
        using var cancellation = new CancellationTokenSource();
        var files = new RecordingFiles(cancellation);
        string waiting = RecordStep("ready") + "SLEEP(30)\n";
        string inner = "TRY\n" + waiting + "FINALLY\n"
            + RecordStep("inner") + "ENDTRY\n";
        string body = variant switch
        {
            "nested" => "TRY\n" + inner + "FINALLY\n"
                + RecordStep("outer") + "ENDTRY\n",
            "active-finally" => "TRY\nTRY\n" + RecordStep("body")
                + "FINALLY\n" + waiting + RecordStep("remaining")
                + "ENDTRY\nFINALLY\n" + RecordStep("outer") + "ENDTRY\n",
            "child" => "TRY\nCALL child\nFINALLY\n"
                + RecordStep("outer") + "ENDTRY\n",
            "unused-child" => "IF FALSE THEN CALL child\n" + inner,
            _ => inner
        };
        bool childOption = variant is "child" or "unused-child";
        string option = enabled ? "OPTION OPTFINALLY\n" : "";
        if (childOption)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"),
                option + inner);
        }

        string main = Path.Combine(directory, "main.uws");
        await File.WriteAllTextAsync(main, (childOption ? "" : option)
            + body + RecordStep("after") + "EXITEXIT\n");
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Interpreter(files: files).RunAsync(
                ScriptCompiler.CompileFile(main), new CallbackScriptHost(_ => { }),
                cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        List<string> expected = [];
        if (variant == "active-finally")
        {
            expected.Add("body");
        }

        expected.Add("ready");
        if (enabled)
        {
            if (variant != "active-finally")
            {
                expected.Add("inner");
            }

            if (variant is "nested" or "active-finally" or "child")
            {
                expected.Add("outer");
            }
        }

        expected.Add("dispose");
        Assert.Equal(expected, files.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupBoundsShareOneTimerAndPreserveOriginalException(
        bool expireTimer)
    {
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException(
            "original", null, cancellation.Token);
        var clock = new ManualClock();
        var files = new RecordingFiles(cancellation);
        var host = new CancellingHost(cancellation, primary, clock, files.Events);
        string cleanup = expireTimer ? "SLEEP(30)" : "WHILE TRUE\nWEND";
        Exception? error = await Record.ExceptionAsync(() =>
            new Interpreter(files: files, timeProvider: clock).RunAsync(
                "OPTION OPTFINALLY\nTRY\nTRY\nPRINT \"body\"\nFINALLY\n"
                + cleanup + "\nENDTRY\nFINALLY\nPRINT \"outer\"\nENDTRY",
                host, cancellation.Token));
        Assert.Same(primary, error);
        Assert.Equal(["body", "dispose"], files.Events);
        Assert.Equal(1, clock.Creations);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.DueTime);
        ManualTimer timer = Assert.IsType<ManualTimer>(clock.Timer);
        Assert.True(timer.Disposed);
        AggregateException failures = Assert.IsType<AggregateException>(
            primary.Data["UWSCSharp.FinallyErrors"]);
        Assert.Equal(2, failures.InnerExceptions.Count);
        Assert.All(failures.InnerExceptions, failure =>
            Assert.IsAssignableFrom(expireTimer
                ? typeof(OperationCanceledException)
                : typeof(InvalidOperationException), failure));
        if (expireTimer)
        {
            Assert.NotEqual(cancellation.Token, host.CleanupToken);
            Assert.True(host.CleanupToken.IsCancellationRequested);
        }
    }

    private static string RecordStep(string key) =>
        $"WRITEINI(\"probe\",\"{key}\",\"1\",\"signal.ini\")\n";

    private sealed class RecordingFiles(CancellationTokenSource cancellation)
        : IScriptFiles, IScriptFileSession
    {
        internal List<string> Events { get; } = [];

        public IScriptFileSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Assert.Equal("WRITEINI", name);
            token.ThrowIfCancellationRequested();
            string key = arguments[1].Text;
            Events.Add(key);
            if (key == "ready")
            {
                cancellation.Cancel();
            }

            return ValueTask.FromResult(Value.Numeric(1));
        }

        public ValueTask DisposeAsync()
        {
            Events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingHost(CancellationTokenSource cancellation,
        OperationCanceledException primary, ManualClock clock,
        List<string> events) : IScriptHost
    {
        internal CancellationToken CleanupToken;

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            events.Add(text);
            cancellation.Cancel();
            throw primary;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            CleanupToken = token;
            Assert.IsType<ManualTimer>(clock.Timer).Fire();
            return Task.Delay(Timeout.Infinite, token);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        internal int Creations;
        internal TimeSpan DueTime;
        internal ManualTimer? Timer;

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            Creations++;
            DueTime = dueTime;
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            Timer = new ManualTimer(callback, state);
            return Timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        internal bool Disposed;

        internal void Fire()
        {
            if (!Disposed)
            {
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
