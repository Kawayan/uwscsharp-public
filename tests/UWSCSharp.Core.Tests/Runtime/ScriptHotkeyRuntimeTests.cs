using System.Collections.Concurrent;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptHotkeyRuntimeTests
{
    [Fact]
    public async Task EmergencyReservationPreservesExistingRegistration()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            registered = SETHOTKEY(65,2,"Pressed")
            reserved = SETHOTKEY(122,7,"Pressed")
            blocked = SETHOTKEY(122,7)
            PRINT VARTYPE(registered)
            PRINT VARTYPE(reserved)
            PRINT VARTYPE(blocked)
            PRINT registered + 0
            PRINT reserved + 0
            PRINT blocked + 0
            PROCEDURE Pressed()
            FEND
            """, output.Add);
        Assert.Equal(["11", "11", "11", "1", "0", "0"], output);
        Assert.Equal(1, automation.Source.Registrations);
        Assert.Equal(0, automation.Source.Removals);
        Assert.Equal(new ScriptHotkey(65, 2), automation.Source.LastKey);
    }

    [Fact]
    public async Task UnregisterResultPreservesLogicalType()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            SETHOTKEY(65,2,"Pressed")
            removed = SETHOTKEY(65,2)
            missing = SETHOTKEY(65,2)
            PRINT VARTYPE(removed)
            PRINT VARTYPE(missing)
            PRINT removed + 0
            PRINT missing + 0
            PROCEDURE Pressed()
            FEND
            """, output.Add);
        Assert.Equal(["11", "11", "1", "1"], output);
        Assert.Equal(1, automation.Source.Removals);
    }

    [Theory]
    [InlineData("122.75,6,\"Pressed\"")]
    [InlineData("-1,6,\"Pressed\"")]
    [InlineData("256,6,\"Pressed\"")]
    [InlineData("122,6.75,\"Pressed\"")]
    [InlineData("122,-1,\"Pressed\"")]
    [InlineData("122,16,\"Pressed\"")]
    [InlineData("122,6,123")]
    [InlineData("122,6,\"\"")]
    [InlineData("122,6,\"Missing\"")]
    [InlineData("122,6,\"FunctionTarget\"")]
    [InlineData("122,6,\"Required\"")]
    public async Task InvalidRegistrationDoesNotChangeExistingSource(string arguments)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync($$"""
            SETHOTKEY(65,2,"Pressed")
            TRY
                SETHOTKEY({{arguments}})
                PRINT "unexpected"
            EXCEPT
                PRINT "rejected"
            ENDTRY
            PROCEDURE Pressed()
            FEND
            PROCEDURE Required(value)
            FEND
            FUNCTION FunctionTarget()
                RESULT=1
            FEND
            """, output.Add);
        Assert.Equal(["rejected"], output);
        Assert.Equal(1, automation.Source.Registrations);
        Assert.Equal(new ScriptHotkey(65, 2), automation.Source.LastKey);
        Assert.Equal(0, automation.Source.Removals);
    }

    [Theory]
    [InlineData("0,1", 0, 1)]
    [InlineData("255,15", 255, 15)]
    [InlineData("\"122\",\"6\"", 122, 6)]
    [InlineData("TRUE,FALSE", 1, 0)]
    [InlineData("122,0", 122, 0)]
    [InlineData("122,1", 122, 1)]
    [InlineData("122,2", 122, 2)]
    [InlineData("122,3", 122, 3)]
    [InlineData("122,4", 122, 4)]
    [InlineData("122,5", 122, 5)]
    [InlineData("122,6", 122, 6)]
    [InlineData("122,8", 122, 8)]
    [InlineData("122,9", 122, 9)]
    [InlineData("122,10", 122, 10)]
    [InlineData("122,11", 122, 11)]
    [InlineData("122,12", 122, 12)]
    [InlineData("122,13", 122, 13)]
    [InlineData("122,14", 122, 14)]
    [InlineData("122,15", 122, 15)]
    [InlineData("121,7", 121, 7)]
    public async Task NumericConversionAndBoundsReachSource(string arguments, int key, int modifiers)
    {
        var automation = new Automation();
        await new Interpreter(windows: automation).RunAsync($$"""
            SETHOTKEY({{arguments}},"Pressed")
            PROCEDURE Pressed()
            FEND
            """, _ => { });
        Assert.Equal(1, automation.Source.Registrations);
        Assert.Equal(new ScriptHotkey(key, modifiers), automation.Source.LastKey);
    }

    [Fact]
    public async Task ProcedureWithDefaultParameterCanBeCalledWithoutArguments()
    {
        var automation = new Automation();
        automation.Source.NotificationsOnRegister = 1;
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            SETHOTKEY(65,2,"Pressed")
            SLEEP(0.01)
            PROCEDURE Pressed(value=9)
                PRINT value
            FEND
            """, output.Add);
        Assert.Equal(["9"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackExitKeepsExitCodeWhetherMainWaitHasCompleted(bool completeMainWait)
    {
        var automation = new Automation();
        var host = new Host();
        host.ShortDelay = _ =>
        {
            if (completeMainWait)
            {
                host.Release.TrySetResult();
            }

            return Task.CompletedTask;
        };
        var interpreter = new Interpreter(windows: automation);
        Task<int> run = interpreter.RunWithExitCodeAsync(ScriptCompiler.Compile("""
            SETHOTKEY(65,0,"Pressed")
            SLEEP(10)
            PRINT "unexpected"
            PROCEDURE Pressed()
                SLEEP(0.1)
                EXITEXIT(7)
            FEND
            """), host, default, ScriptRunOptions.Empty);
        await host.Sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        automation.Source.Notify!();
        Assert.Equal(7, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(host.Output);
        Assert.Equal(1, automation.Source.Disposals);
    }

    [Fact]
    public async Task SleepDeliversCallbacksInOrderWithoutReentry()
    {
        var automation = new Automation();
        var host = new Host();
        int shortDelays = 0;
        host.ShortDelay = token =>
        {
            if (Interlocked.Increment(ref shortDelays) == 1)
            {
                automation.Source.Notify!();
            }

            return Task.CompletedTask;
        };
        Task run = new Interpreter(windows: automation).RunAsync("""
            SETHOTKEY(65,MOD_CONTROL,"Pressed")
            SLEEP(10)
            PRINT "done"
            PROCEDURE Pressed()
                PRINT "start:"+HOTKEY_VK+":"+HOTKEY_MOD
                SLEEP(0.01)
                PRINT "end"
            FEND
            """, host);
        await host.Sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        automation.Source.Notify!();
        await host.TwoCallbacks.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["start:65:2", "end", "start:65:2", "end", "done"], host.Output);
        Assert.Equal(1, automation.Source.Disposals);
    }

    [Fact]
    public async Task OverflowCancelsPendingHostOperationAndKeepsItsCause()
    {
        var automation = new Automation();
        var host = new Host();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ShortDelay = token =>
        {
            callbackEntered.SetResult();
            return Task.Delay(Timeout.Infinite, token);
        };
        Task run = new Interpreter(windows: automation).RunAsync("""
            SETHOTKEY(65,0,"Pressed")
            SLEEP(10)
            PROCEDURE Pressed()
                SLEEP(0.1)
            FEND
            """, host, default, new ScriptRunOptions([]) { PendingHotkeyLimit = 1 });
        await host.Sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        automation.Source.Notify!();
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        automation.Source.Notify!();
        automation.Source.Notify!();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("保留上限", error.Message);
        Assert.Equal(1, automation.Source.Disposals);
    }

    [Fact]
    public async Task CallbackFailureIsNotReplacedByCancellationOfSleep()
    {
        var automation = new Automation();
        var host = new Host();
        Task run = new Interpreter(windows: automation).RunAsync("""
            SETHOTKEY(65,0,"Pressed")
            SLEEP(10)
            PROCEDURE Pressed()
                PRINT missing_variable
            FEND
            """, host);
        await host.Sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        automation.Source.Notify!();
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("missing_variable", error.Message);
        Assert.Equal(1, automation.Source.Disposals);
    }

    [Fact]
    public async Task OmittedKeyAndUnregistrationReachSource()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            PRINT SETHOTKEY(,MOD_CONTROL+MOD_SHIFT,"Pressed")
            PRINT SETHOTKEY(,MOD_CONTROL+MOD_SHIFT)
            PROCEDURE Pressed()
            FEND
            """, output.Add);
        Assert.Equal(["1", "1"], output);
        Assert.Equal(new ScriptHotkey(0, 6), automation.Source.LastKey);
        Assert.Equal(1, automation.Source.Removals);
    }

    [Fact]
    public async Task WorkerCannotRegisterAndInvalidArityDoesNotEvaluateArguments()
    {
        var automation = new Automation();
        var output = new ConcurrentQueue<string>();
        await new Interpreter(windows: automation).RunAsync("""
            PUBLIC calls=0
            TRY
                SETHOTKEY(65,0,"Pressed",Tick())
            EXCEPT
                PRINT calls
            ENDTRY
            THREAD Worker()
            SLEEP(0.1)
            PROCEDURE Worker()
                TRY
                    SETHOTKEY(65,0,"Pressed")
                EXCEPT
                    PRINT "worker rejected"
                ENDTRY
            FEND
            PROCEDURE Pressed()
            FEND
            FUNCTION Tick()
                calls=calls+1
                RESULT=0
            FEND
            """, output.Enqueue);
        Assert.Contains("0", output);
        Assert.Contains("worker rejected", output);
        Assert.Null(automation.Source.Notify);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceFailureCancelsSleepAndKeepsCause(bool failDispose)
    {
        var automation = new Automation();
        automation.Source.FailDispose = failDispose;
        var host = new Host();
        Task run = new Interpreter(windows: automation).RunAsync("""
            SETHOTKEY(65,0,"Pressed")
            SLEEP(10)
            PROCEDURE Pressed()
            FEND
            """, host);
        await host.Sleeping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        automation.Source.SetFailure(new InvalidOperationException("source stopped"));
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("source stopped", error.Message);
        Assert.Equal(1, automation.Source.Disposals);
    }

    [Fact]
    public async Task SourceFailureStopsPausedScriptWithoutResumingIt()
    {
        using var pause = new ScriptPauseControl();
        var automation = new Automation();
        var host = new Host { OnOutput = _ => pause.Pause() };
        Task run = new Interpreter(windows: automation).RunAsync("""
            SETHOTKEY(65,0,"Pressed")
            PRINT "pause"
            SLEEP(10)
            PROCEDURE Pressed()
            FEND
            """, host, default, new ScriptRunOptions([]) { PauseControl = pause });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!pause.IsPaused)
        {
            await Task.Delay(1, timeout.Token);
        }
        automation.Source.SetFailure(new InvalidOperationException("source stopped"));
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("source stopped", error.Message);
        Assert.Equal(["pause"], host.Output);
        Assert.Equal(1, automation.Source.Disposals);
    }

    [Fact]
    public async Task SourceFailureDuringFinalRegistrationIsReported()
    {
        var automation = new Automation();
        automation.Source.FailOnRegister = true;
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
            new Interpreter(windows: automation).RunAsync("""
                SETHOTKEY(65,0,"Pressed")
                PROCEDURE Pressed()
                FEND
                """, _ => { }));
        Assert.Contains("source stopped", error.Message);
        Assert.Equal(1, automation.Source.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverflowDuringFinalRegistrationCannotFinishSuccessfully(bool failDispose)
    {
        var automation = new Automation();
        automation.Source.NotificationsOnRegister = 2;
        automation.Source.FailDispose = failDispose;
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() =>
            new Interpreter(windows: automation).RunAsync("""
                SETHOTKEY(65,0,"Pressed")
                PROCEDURE Pressed()
                FEND
                """, _ => { }, default,
                new ScriptRunOptions([]) { PendingHotkeyLimit = 1 }));
        Assert.Contains("保留上限", error.Message);
        Assert.Equal(1, automation.Source.Disposals);
    }

    [Fact]
    public void HostLimitMustBePositiveAndDefaultsTo1024()
    {
        Assert.Equal(1024, new ScriptRunOptions([]).PendingHotkeyLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptRunOptions([]) { PendingHotkeyLimit = 0 });
    }

    private sealed class Host : IScriptHost
    {
        internal readonly ConcurrentQueue<string> Output = new();
        internal readonly TaskCompletionSource Sleeping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource TwoCallbacks = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<CancellationToken, Task>? ShortDelay;
        internal Action<string>? OnOutput;
        private int ends;

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            text = text.TrimEnd('\r', '\n');
            Output.Enqueue(text);
            OnOutput?.Invoke(text);
            if (text == "end" && Interlocked.Increment(ref ends) == 2)
            {
                TwoCallbacks.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (duration.TotalSeconds >= 1)
            {
                Sleeping.TrySetResult();
                return Release.Task.WaitAsync(token);
            }

            return ShortDelay?.Invoke(token) ?? Task.CompletedTask;
        }
    }

    private sealed class Automation : IWindowsAutomation
    {
        internal readonly Source Source = new();
        public IWindowsAutomationSession CreateSession() => new Session(Source);
    }

    private sealed class Session(Source source) : IWindowsAutomationSession
    {
        public IWindowsAutomationSession Fork() => new Session(source);
        public IScriptHotkeySource CreateHotkeySource() => source;
        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken token) => name == BuiltinFunctionNames.VarType
                ? ValueTask.FromResult(VariantTypeRequest.Query(arguments[0]))
                : throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Source : IScriptHotkeySource
    {
        internal Action? Notify;
        internal ScriptHotkey LastKey;
        internal int Disposals;
        internal int Removals;
        internal int Registrations;
        internal int NotificationsOnRegister;
        internal bool FailDispose;
        internal bool FailOnRegister;
        private Exception? failure;

        public Exception? Failure => Volatile.Read(ref failure);

        internal void SetFailure(Exception error) => Volatile.Write(ref failure, error);

        public bool TryRegister(ScriptHotkey key, Action notify)
        {
            Registrations++;
            LastKey = key;
            Notify = notify;
            if (FailOnRegister)
            {
                SetFailure(new InvalidOperationException("source stopped"));
            }

            for (int index = 0; index < NotificationsOnRegister; index++)
            {
                notify();
            }

            return true;
        }

        public bool TryUnregister(ScriptHotkey key)
        {
            LastKey = key;
            Removals++;
            Notify = null;
            return true;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return FailDispose
                ? ValueTask.FromException(new InvalidOperationException("dispose"))
                : ValueTask.CompletedTask;
        }
    }
}
