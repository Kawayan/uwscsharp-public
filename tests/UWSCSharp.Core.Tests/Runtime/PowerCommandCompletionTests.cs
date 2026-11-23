using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class PowerCommandCompletionTests
{
    [Theory]
    [InlineData("FALSE", false)]
    [InlineData("TRUE", true)]
    public async Task RestartNotifiesAfterFinallyAndFileCleanup(
        string argument, bool restartScript)
    {
        var events = new List<string>();
        var options = new ScriptRunOptions([])
        {
            PowerOperation = request =>
            {
                Assert.Equal(PowerCommand.RestartApplication, request.Command);
                Assert.Equal(restartScript, request.RestartScript);
                events.Add("prepare");
                return () => events.Add("restart");
            }
        };
        var runner = new Interpreter(files: new Files(events));
        int result = await runner.RunWithExitCodeAsync($"""
            TRY
                POFF(16384, {argument})
                PRINT "unreachable"
            FINALLY
                PRINT "finally"
            ENDTRY
            PRINT "unreachable"
            """, new CallbackScriptHost(events.Add),
            default, options);

        Assert.Equal(0, result);
        Assert.Equal(["prepare", "finally", "dispose", "restart"], events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailurePreventsRestart(bool failDuringDispose)
    {
        var events = new List<string>();
        var options = new ScriptRunOptions([])
        {
            PowerOperation = _ => () => events.Add("restart")
        };
        var runner = new Interpreter(files: new Files(events, failDuringDispose));
        string cleanup = failDuringDispose ? "PRINT 1" : "PRINT INT(NULL)";
        Task<int> run = runner.RunWithExitCodeAsync($"""
                TRY
                    POFF(16384)
                FINALLY
                    {cleanup}
                ENDTRY
                """, new CallbackScriptHost(_ => { }),
            default, options);

        if (failDuringDispose)
        {
            await Assert.ThrowsAsync<IOException>(() => run);
        }
        else
        {
            await Assert.ThrowsAsync<ScriptException>(() => run);
        }

        Assert.Equal(["dispose"], events);
    }

    [Fact]
    public async Task OrdinaryOperationDoesNotStopScript()
    {
        var events = new List<string>();
        var options = new ScriptRunOptions([])
        {
            PowerOperation = request =>
            {
                Assert.Equal(PowerCommand.MonitorOn, request.Command);
                events.Add("display");
                return null;
            }
        };
        await new Interpreter().RunWithExitCodeAsync(
            "POFF(4096)\nPRINT 1", new CallbackScriptHost(events.Add),
            default, options);

        Assert.Equal(["display", "1"], events);
    }

    [Fact]
    public async Task ExistingUserRoutineRemainsAvailableWithoutPowerHandler()
    {
        var output = new List<string>();
        await new Interpreter().RunAsync("""
            POFF(3)
            PROCEDURE POFF(value)
                PRINT value
            FEND
            """, new CallbackScriptHost(output.Add));

        Assert.Equal(["3"], output);
    }

    private sealed class Files(List<string> events, bool fail = false)
        : IScriptFiles, IScriptFileSession
    {
        public IScriptFileSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException();

        public ValueTask DisposeAsync()
        {
            events.Add("dispose");
            return fail
                ? ValueTask.FromException(new IOException("ファイルの解放に失敗しました。"))
                : ValueTask.CompletedTask;
        }
    }
}
