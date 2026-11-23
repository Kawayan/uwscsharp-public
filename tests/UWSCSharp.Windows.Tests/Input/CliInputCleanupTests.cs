using System.Diagnostics;
using UWSCSharp.Cli;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class CliInputCleanupTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--input-cleanup=other")]
    [InlineData("--input-cleanup=manual")]
    public async Task CommonApplicationRejectsInvalidLaunchBeforeExecution(string option)
    {
        TextWriter originalError = Console.Error;
        System.Text.Encoding originalEncoding = Console.OutputEncoding;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            int result = await CliApplication.RunAsync(option.Length == 0 ? [] : [option]);
            Assert.Equal(2, result);
            Assert.Contains("Usage:", error.ToString());
        }
        finally
        {
            Console.OutputEncoding = originalEncoding;
            Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData("TRUE", false, 0, 1)]
    [InlineData("TRUE", true, 1, 1)]
    [InlineData("FALSE", false, 1, 0)]
    [InlineData("TRUE", false, 1, 0, true)]
    public async Task CommonApplicationHandlesScriptRestart(
        string argument, bool launchFails, int expectedExit, int expectedStarts,
        bool finallyFails = false)
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string script = Path.Combine(directory, "script with spaces.uws");
            string cleanup = finallyFails ? "PRINT INT(NULL)" : "PRINT 1";
            await File.WriteAllTextAsync(script, $"""
                TRY
                    POFF(P_UWSC_REEXEC, {argument})
                    PRINT INT(NULL)
                FINALLY
                    {cleanup}
                ENDTRY
                """);

            var runtime = new RestartRuntime(launchFails);
            int result = await CliApplication.RunAsync([script, "", "a b"], runtime);
            Assert.Equal(expectedExit, result);
            Assert.Equal(expectedStarts, runtime.Starts.Count);
            if (expectedStarts == 1)
            {
                ProcessStartInfo start = Assert.Single(runtime.Starts);
                Assert.Equal([script, "", "a b"], start.ArgumentList.TakeLast(3));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CommonApplicationRoutesPowerCommandsAndKeepsRunning()
    {
        string script = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".uws");
        try
        {
            await File.WriteAllTextAsync(script, """
                POFF(P_POWEROFF OR P_FORCE)
                POFF(P_MONIPOWER3)
                """);
            var runtime = new RestartRuntime(false);
            int result = await CliApplication.RunAsync([script], runtime);

            Assert.Equal(0, result);
            Assert.Equal([
                new(PowerCommand.PowerOff, true, false),
                new(PowerCommand.MonitorOn, false, false)
            ], runtime.PowerCommands);
            Assert.Empty(runtime.Starts);
        }
        finally
        {
            File.Delete(script);
        }
    }

    private sealed class RestartRuntime(bool launchFails) : CliWindowsRuntime
    {
        internal List<ProcessStartInfo> Starts
        {
            get;
        } = [];

        internal List<PowerCommandRequest> PowerCommands
        {
            get;
        } = [];

        internal override void ExecutePowerCommand(PowerCommandRequest request) =>
            PowerCommands.Add(request);

        internal override void StartReplacement(ProcessStartInfo start)
        {
            Starts.Add(start);
            if (launchFails)
            {
                throw new InvalidOperationException("代替起動に失敗しました。");
            }
        }
    }

    [Fact]
    public void OnlyTheLeadingHostOptionIsConsumed()
    {
        string[] args = [CliLaunchOptions.ManualCleanupOption, "job.uws", "", "001",
            CliLaunchOptions.ManualCleanupOption];
        var parsed = CliLaunchOptions.Parse(args, true);
        Assert.True(parsed.ManualCleanup);
        Assert.Equal(args.Skip(1), parsed.ScriptArguments);
        args[1] = "changed";
        Assert.Equal("job.uws", parsed.ScriptArguments[0]);
        Assert.False(CliLaunchOptions.Parse(["job.uws", CliLaunchOptions.ManualCleanupOption], true).ManualCleanup);
        Assert.False(CliLaunchOptions.Parse(["./--input-cleanup=manual"], true).ManualCleanup);
    }

    [Fact]
    public void MissingUnknownAndUnsupportedOptionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CliLaunchOptions.Parse([], true));
        Assert.Throws<ArgumentException>(() => CliLaunchOptions.Parse([CliLaunchOptions.ManualCleanupOption], true));
        Assert.Throws<ArgumentException>(() => CliLaunchOptions.Parse(["--input-cleanup=other", "job.uws"], true));
        Assert.Throws<ArgumentException>(() => CliLaunchOptions.Parse([CliLaunchOptions.ManualCleanupOption, "job.uws"], false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoPendingOwnerDoesNotOpenInput(bool manual)
    {
        var cleanup = new Cleanup { PendingCount = 0 };
        using var error = new StringWriter();
        int result = await Controller(cleanup, error).CompleteAsync(37, manual,
            new CliStopRequests(() => throw new InvalidOperationException()),
            () => throw new InvalidOperationException("入力を開いてはいけません。"));
        Assert.Equal(37, result);
        Assert.Equal(0, cleanup.Retries);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task DefaultModeReportsPendingWithoutRetryOrInput()
    {
        var cleanup = new Cleanup();
        using var error = new StringWriter();
        int result = await Controller(cleanup, error).CompleteAsync(0, false,
            new CliStopRequests(() => { }), () => throw new InvalidOperationException());
        Assert.Equal(1, result);
        Assert.Equal(0, cleanup.Retries);
        Assert.Contains("未回収所有者: 1", error.ToString());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(37, 37)]
    [InlineData(130, 130)]
    public async Task ExitPreservesAnExistingFailure(int original, int expected)
    {
        var cleanup = new Cleanup();
        using var error = new StringWriter();
        Input input = Commands("exit\r");
        int result = await Controller(cleanup, error).CompleteAsync(original, true,
            new CliStopRequests(() => { }), () => input);
        Assert.Equal(expected, result);
        Assert.Equal(0, cleanup.Retries);
        Assert.True(input.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(130)]
    public async Task RecoveryPreservesTheScriptResultAndHistoricalDiagnosis(int original)
    {
        var cleanup = new Cleanup();
        cleanup.Retry = () =>
        {
            cleanup.PendingCount = 0;
            return Task.FromResult(new CliCleanupAttempt([new InvalidOperationException("過去の障害")], 0));
        };
        using var error = new StringWriter();
        Input input = Commands(" \tReTrY \t\r");
        int result = await Controller(cleanup, error).CompleteAsync(original, true,
            new CliStopRequests(() => { }), () => input);
        Assert.Equal(original, result);
        Assert.Equal(1, cleanup.Retries);
        Assert.Contains("過去の障害", error.ToString());
        Assert.Contains("未回収所有者: 0", error.ToString());
        Assert.True(input.Disposed);
    }

    [Fact]
    public async Task InputDuringRetryDoesNotBecomeAnotherRequest()
    {
        var cleanup = new Cleanup();
        using var error = new StringWriter();
        var input = new Input(Empty(), Keys("retry\r"), Keys("retry\r"), Empty(), Keys("exit\r"));
        await Controller(cleanup, error).CompleteAsync(1, true,
            new CliStopRequests(() => { }), () => input);
        Assert.Equal(1, cleanup.Retries);
    }

    [Fact]
    public async Task NonTextEventsDoNotArmThePromptBeforeTheQueueIsEmpty()
    {
        var cleanup = new Cleanup();
        using var error = new StringWriter();
        var input = new Input(new CliRecoveryBatch([], true), Keys("retry\r"), Empty(), Keys("exit\r"));
        await Controller(cleanup, error).CompleteAsync(1, true,
            new CliStopRequests(() => { }), () => input);
        Assert.Equal(0, cleanup.Retries);
    }

    [Fact]
    public async Task RepeatedEnterInOneBatchDoesNotRepeatRecovery()
    {
        var cleanup = new Cleanup();
        using var error = new StringWriter();
        var input = new Input(Empty(), new CliRecoveryBatch(
            [.. "retry".Select(c => new CliRecoveryKey(c)), new('\r', 10)], true), Empty(), Keys("exit\r"));
        await Controller(cleanup, error).CompleteAsync(1, true,
            new CliStopRequests(() => { }), () => input);
        Assert.Equal(1, cleanup.Retries);
    }

    [Theory]
    [InlineData("retry                                                                 \r")]
    [InlineData("retry\u001b\r")]
    public async Task InvalidLinesDoNotRequestRecovery(string command)
    {
        var cleanup = new Cleanup();
        using var error = new StringWriter();
        var input = new Input(Empty(), Keys(command), Empty(), Keys("exit\r"));
        await Controller(cleanup, error).CompleteAsync(1, true,
            new CliStopRequests(() => { }), () => input);
        Assert.Equal(0, cleanup.Retries);
    }

    [Fact]
    public async Task CancellationDuringRetryWaitsForTheAttemptAndReturns130()
    {
        var cleanup = new Cleanup();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CliCleanupAttempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        cleanup.Retry = () =>
        {
            started.SetResult();
            return release.Task;
        };
        int scriptStops = 0;
        var stop = new CliStopRequests(() => scriptStops++);
        using var error = new StringWriter();
        Input input = Commands("retry\r");
        Task<int> run = Controller(cleanup, error).CompleteAsync(37, true, stop, () => input);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Request();
            Assert.False(run.IsCompleted);
        }
        finally
        {
            release.TrySetResult(new CliCleanupAttempt([], 1));
        }

        Assert.Equal(130, await run);
        Assert.Equal(0, scriptStops);
        Assert.True(input.Disposed);
    }

    [Fact]
    public async Task ScriptCancellationDoesNotCancelTheLaterRecoveryWait()
    {
        int scriptStops = 0;
        var stop = new CliStopRequests(() => scriptStops++);
        stop.Request();
        var cleanup = new Cleanup();
        using var error = new StringWriter();
        Input input = Commands("exit\r");
        Assert.Equal(130, await Controller(cleanup, error).CompleteAsync(130, true, stop, () => input));
        Assert.Equal(1, scriptStops);
        Assert.True(input.Reads > 0);
        Assert.False(stop.ExitRequested);
        stop.Finish();
        stop.Request();
        Assert.Equal(1, scriptStops);
    }

    [Theory]
    [InlineData("exit\r")]
    [InlineData("retry\r")]
    public async Task CompletionDuringInputDoesNotChangeTheResultOrRetry(string command)
    {
        var cleanup = new Cleanup();
        using var error = new StringWriter();
        Input input = Commands(command);
        input.OnRead = reads =>
        {
            if (reads == 2)
            {
                cleanup.PendingCount = 0;
            }
        };
        int result = await Controller(cleanup, error).CompleteAsync(0, true,
            new CliStopRequests(() => { }), () => input);
        Assert.Equal(0, result);
        Assert.Equal(0, cleanup.Retries);
    }

    [Fact]
    public async Task InputFailureStillDisposesTheInputAndPreservesFailureCode()
    {
        var cleanup = new Cleanup();
        using var error = new StringWriter();
        Input input = Commands("exit\r");
        input.OnRead = _ => throw new IOException("端末消失");
        int result = await Controller(cleanup, error).CompleteAsync(37, true,
            new CliStopRequests(() => { }), () => input);
        Assert.Equal(37, result);
        Assert.True(input.Disposed);
        Assert.Contains("端末消失", error.ToString());
    }

    private static CliInputCleanupController Controller(Cleanup cleanup, TextWriter error) =>
        new(cleanup, error, static () => Task.CompletedTask);

    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(true, 37, 37)]
    public async Task UnwritableDiagnosticDoesNotLeaveAnInvisibleWaitOrReplaceTheExitCode(
        bool atPrompt, int original, int expected)
    {
        var cleanup = new Cleanup();
        using var error = new FaultWriter(atPrompt);
        Input input = Commands("exit\r");
        bool opened = false;
        int result = await Controller(cleanup, error).CompleteAsync(original, true,
            new CliStopRequests(() => { }), () =>
            {
                opened = true;
                return input;
            });
        Assert.Equal(expected, result);
        Assert.Equal(atPrompt, opened);
        Assert.Equal(0, cleanup.Retries);
        Assert.Equal(atPrompt, input.Disposed);
    }

    private sealed class FaultWriter(bool atPrompt) : StringWriter
    {
        private bool failed;

        public override void Write(string? value)
        {
            if (failed || (atPrompt && value?.StartsWith("retry:", StringComparison.Ordinal) == true))
            {
                failed = true;
                throw new IOException("診断先が消失しました。");
            }

            base.Write(value);
        }

        public override void WriteLine(string? value)
        {
            if (failed || !atPrompt)
            {
                failed = true;
                throw new IOException("診断先が消失しました。");
            }

            base.WriteLine(value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationFromTheFirstRecoveryNotificationAlreadyBelongsToRecovery(bool failOutput)
    {
        int scriptStops = 0;
        var stop = new CliStopRequests(() => scriptStops++);
        using var error = new NotificationWriter(stop.Request, failOutput);
        bool opened = false;
        int code = await Controller(new Cleanup(), error).CompleteAsync(37, true, stop, () =>
        {
            opened = true;
            return Commands("exit\r");
        });
        Assert.Equal(130, code);
        Assert.Equal(0, scriptStops);
        Assert.False(opened);
    }

    private sealed class NotificationWriter(Action request, bool fail) : StringWriter
    {
        private bool sent;

        public override void WriteLine(string? value)
        {
            if (!sent && value?.StartsWith("未回収所有者", StringComparison.Ordinal) == true)
            {
                sent = true;
                request();
                if (fail)
                {
                    throw new IOException("診断先消失と取消");
                }
            }

            base.WriteLine(value);
        }
    }

    private static CliRecoveryBatch Empty() => new([], false);

    private static CliRecoveryBatch Keys(string text) =>
        new([.. text.Select(c => new CliRecoveryKey(c))], true);

    private static Input Commands(string command) => new(Empty(), Keys(command));

    // 端末やOS入力を使わず、回収状態と明示要求だけを注入します。
    private sealed class Cleanup : ICliInputCleanup
    {
        public int PendingCount { get; set; } = 1;
        internal int Retries;
        internal Func<Task<CliCleanupAttempt>>? Retry;

        public Task<CliCleanupAttempt> RetryAsync()
        {
            Retries++;
            return Retry?.Invoke() ?? Task.FromResult(new CliCleanupAttempt([], PendingCount));
        }
    }

    private sealed class Input(params CliRecoveryBatch[] batches) : ICliRecoveryInput
    {
        private readonly Queue<CliRecoveryBatch> queued = new(batches);
        internal int Reads;
        internal bool Disposed;
        internal Action<int>? OnRead;

        public CliRecoveryBatch ReadBatch()
        {
            Reads++;
            if (Reads > 64)
            {
                throw new InvalidOperationException("検査の読取り上限を超えました。");
            }

            OnRead?.Invoke(Reads);
            return queued.TryDequeue(out CliRecoveryBatch batch) ? batch : Empty();
        }

        public void Dispose() => Disposed = true;
    }
}
