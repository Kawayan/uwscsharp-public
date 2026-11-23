using System.Collections.Concurrent;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScriptComEventRuntimeTests
{
    private sealed class Automation : IWindowsAutomation
    {
        internal readonly ConcurrentDictionary<string, Func<IReadOnlyList<Value>, CancellationToken,
            ValueTask<IReadOnlyList<Value>>>> Callbacks = new(StringComparer.OrdinalIgnoreCase);
        internal readonly TaskCompletionSource Registered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource WaitStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Value>? PendingOperation;
        internal int Registrations;
        internal int Removals;
        internal bool FireOnRegister;
        internal readonly Exception ComFailure = new InvalidOperationException("COM fixture");

        public IWindowsAutomationSession CreateSession() => new Session(this);

        private sealed class Session(Automation source) : IWindowsAutomationSession
        {
            public IWindowsAutomationSession Fork() => new Session(source);

            public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
                CancellationToken token) => ValueTask.FromResult(Value.FromObject(source));

            public async ValueTask<Value> InvokeMemberAsync(Value target, string name,
                IReadOnlyList<Value> arguments, Value? assigned, CancellationToken token)
            {
                if (name == "Fail")
                {
                    throw source.ComFailure;
                }

                if (name == "Echo")
                {
                    return Value.Numeric(7);
                }

                if (name == "Wait")
                {
                    source.WaitStarted.TrySetResult();
                    return await source.PendingOperation!.Task;
                }

                if (!source.Callbacks.TryGetValue("Changed",
                    out Func<IReadOnlyList<Value>, CancellationToken,
                        ValueTask<IReadOnlyList<Value>>>? callback))
                {
                    return Value.Numeric(0);
                }

                try
                {
                    IReadOnlyList<Value> result = await callback(
                        [arguments[0], Value.Numeric(0)], token).AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    return result[1];
                }
                catch (Exception)
                {
                    throw source.ComFailure;
                }
            }

            public async ValueTask SetComEventAsync(Value target, string? interfaceName,
                string? eventName, Func<IReadOnlyList<Value>, CancellationToken,
                    ValueTask<IReadOnlyList<Value>>>? callback, CancellationToken token)
            {
                if (callback is null)
                {
                    source.Callbacks.Clear();
                    source.Removals++;
                    return;
                }

                source.Callbacks[eventName!] = callback;
                source.Registrations++;
                source.Registered.TrySetResult();
                if (source.FireOnRegister)
                {
                    await callback([Value.Numeric(42), Value.Numeric(0)], token).AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
            }

            public bool IsComError(Exception failure) => ReferenceEquals(failure, source.ComFailure);

            public string? GetObjectScriptErrorMessage(Exception failure) =>
                IsComError(failure) ? failure.Message : null;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task PauseDefersExternalNotificationUntilResume()
    {
        var automation = new Automation();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pause = new ScriptPauseControl();
        var output = new ConcurrentQueue<string>();
        Task run = new Interpreter(windows: automation).RunAsync("""
            DIM obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"Events","Changed","Changed")
            SLEEP(100)
            PROCEDURE Changed()
                PRINT EVENT_PRM[0]
                EVENT_PRM[1]=TRUE
            FEND
            """, output.Enqueue, cancellation.Token,
            new ScriptRunOptions([]) { PauseControl = pause });
        try
        {
            await automation.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pause.Pause();
            await WaitUntilPaused(pause);
            Task<IReadOnlyList<Value>> notification = automation.Callbacks["Changed"](
                [Value.Numeric(42), Value.Numeric(0)], default).AsTask();
            Assert.False(notification.IsCompleted);
            Assert.Empty(output);
            pause.Resume();
            IReadOnlyList<Value> result = await notification.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, result[1].AsNumber());
            Assert.Equal(["42"], output);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact]
    public async Task CancellationWhilePausedReleasesPendingNotificationWithoutRunningIt()
    {
        var automation = new Automation();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pause = new ScriptPauseControl();
        var output = new ConcurrentQueue<string>();
        Task run = new Interpreter(windows: automation).RunAsync("""
            DIM obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"Events","Changed","Changed")
            SLEEP(100)
            PROCEDURE Changed()
                PRINT "unexpected"
            FEND
            """, output.Enqueue, cancellation.Token,
            new ScriptRunOptions([]) { PauseControl = pause });
        try
        {
            await automation.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pause.Pause();
            await WaitUntilPaused(pause);
            Task<IReadOnlyList<Value>> notification = automation.Callbacks["Changed"](
                [Value.Numeric(42), Value.Numeric(0)], default).AsTask();
            Assert.False(notification.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification);
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Empty(output);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact]
    public async Task SimultaneousCallbackAndHostWaitCancellationHasNoCleanupFailure()
    {
        var automation = new Automation();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = new Interpreter(windows: automation).RunAsync("""
            DIM obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"Events","Changed","Changed")
            SLEEP(100)
            PROCEDURE Changed()
                PRINT "entered"
                SLEEP(100)
            FEND
            """, _ => entered.TrySetResult(), cancellation.Token);
        try
        {
            await automation.Registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<IReadOnlyList<Value>> notification = automation.Callbacks["Changed"](
                [Value.Numeric(42), Value.Numeric(0)], default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification);
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.False(failure.Data.Contains("UWSCSharp.FinallyErrors"));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact]
    public async Task QuittingRegistrationThreadDuringCallbackKeepsOtherThreadsRunning()
    {
        var automation = new Automation();
        var output = new ConcurrentQueue<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await new Interpreter(windows: automation).RunAsync("""
            PUBLIC obj=CREATEOLEOBJ("fixture")
            PUBLIC ready=FALSE
            PUBLIC entered=FALSE
            PUBLIC stopped=FALSE
            THREAD Work()
            WHILE !ready
                SLEEP(0.001)
            WEND
            THREAD Stopper()
            TRY
                PRINT obj.Fire(42)
            EXCEPT
                PRINT "provider-error"
            ENDTRY
            WHILE !stopped
                SLEEP(0.001)
            WEND
            PRINT "done"
            PROCEDURE Work()
                OLEEVENT(obj,"Events","Changed","Changed")
                ready=TRUE
                WHILE TRUE
                    SLEEP(0.001)
                WEND
            FEND
            PROCEDURE Changed()
                entered=TRUE
                SLEEP(100)
                PRINT "unexpected"
            FEND
            PROCEDURE Stopper()
                WHILE !entered
                    SLEEP(0.001)
                WEND
                CTRLTRD(0,TRD_QUIT)
                stopped=TRUE
            FEND
            """, output.Enqueue, timeout.Token);
        Assert.Equal(["provider-error", "done"], output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CallbackCancellationKeepsIndependentOperationFailures(int kind)
    {
        var operation = new TaskCompletionSource<Value>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var automation = new Automation { PendingOperation = operation };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var other = new CancellationTokenSource();
        if (kind == 1)
        {
            other.Cancel();
        }

        Exception operationFailure = kind switch
        {
            0 => new OperationCanceledException(cancellation.Token),
            1 or 2 => new OperationCanceledException(other.Token),
            _ => new InvalidOperationException("independent failure")
        };
        if (kind == 0)
        {
            operationFailure.Data["diagnostic"] = "preserve";
        }

        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = new Interpreter(windows: automation).RunAsync("""
            DIM obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"Events","Changed","Changed")
            PRINT obj.Wait()
            PROCEDURE Changed()
                PRINT "entered"
                SLEEP(100)
            FEND
            """, _ => entered.TrySetResult(), cancellation.Token);
        try
        {
            await automation.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<IReadOnlyList<Value>> notification = automation.Callbacks["Changed"](
                [Value.Numeric(42), Value.Numeric(0)], default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            operation.SetException(operationFailure);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notification);
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Contains(operationFailure,
                Assert.IsType<AggregateException>(
                    failure.Data["UWSCSharp.FinallyErrors"]).InnerExceptions);
        }
        finally
        {
            cancellation.Cancel();
            operation.TrySetException(operationFailure);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    private static async Task WaitUntilPaused(ScriptPauseControl pause)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!pause.IsPaused)
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    [Fact]
    public async Task NestedProcedureRestoresArgumentsAndWritesBackSynchronously()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            PUBLIC obj=CREATEOLEOBJ("fixture")
            PUBLIC depth=0
            PRINT "["+OLEEVENT(obj,"Events","Changed","Changed")+"]"
            PRINT obj.Fire(42)
            PRINT "["+OLEEVENT(obj)+"]"
            PRINT obj.Fire(1)
            PROCEDURE Changed()
                PRINT EVENT_PRM[0]
                IF depth=0
                    depth=1
                    PRINT obj.Fire(99)
                    PRINT EVENT_PRM[0]
                ENDIF
                EVENT_PRM[1]=TRUE
            FEND
            """, output.Add);
        Assert.Equal(["[]", "42", "99", "1", "42", "1", "[]", "0"], output);
        Assert.Equal(1, automation.Registrations);
        Assert.Equal(1, automation.Removals);
    }

    [Fact]
    public async Task CallbackDuringRegistrationCanCallAnotherComMember()
    {
        var automation = new Automation { FireOnRegister = true };
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            PUBLIC obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"Events","Changed","Changed")
            PROCEDURE Changed()
                PRINT obj.Echo()
                EVENT_PRM[1]=TRUE
            FEND
            """, output.Add);
        Assert.Equal(["7"], output);
    }

    [Fact]
    public async Task ProcedureFailureIsNotSuppressedAsProviderComFailure()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            DIM obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"Events","Changed","Changed")
            COM_ERR_IGN
            TRY
                PRINT obj.Fire(42)
            EXCEPT
                PRINT "caught"
                PRINT COM_ERR_FLG
            ENDTRY
            COM_ERR_RET
            PROCEDURE Changed()
                DIM items[0]
                PRINT items[1]
            FEND
            """, output.Add);
        Assert.Equal(["caught", "0"], output);
    }

    [Fact]
    public async Task ExitFromCallbackPreservesExitCode()
    {
        var automation = new Automation();
        int code = await new Interpreter(windows: automation).RunWithExitCodeAsync("""
            DIM obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"Events","Changed","Changed")
            obj.Fire(42)
            PROCEDURE Changed()
                EXITEXIT 7
            FEND
            """, new CallbackScriptHost(_ => { }), default, ScriptRunOptions.Empty);
        Assert.Equal(7, code);
    }

    [Fact]
    public async Task CallbackComFailureUsesRegistrationThreadsErrorState()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            PUBLIC obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"Events","Changed","Changed")
            COM_ERR_IGN
            PRINT obj.Fire(42)
            PRINT COM_ERR_FLG
            COM_ERR_RET
            PROCEDURE Changed()
                PRINT obj.Fail()
                EVENT_PRM[1]=TRUE
            FEND
            """, output.Add);
        Assert.Equal(["-999999", "1", "1"], output);
    }

    [Fact]
    public async Task CallbackCanRestoreComErrorsForTheRegisteringThread()
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            PUBLIC obj=CREATEOLEOBJ("fixture")
            OLEEVENT(obj,"Events","Changed","Changed")
            COM_ERR_IGN
            PRINT obj.Fail()
            PRINT obj.Fire(42)
            PRINT COM_ERR_FLG
            TRY
                obj.Fail()
                PRINT "unexpected"
            EXCEPT
                PRINT COM_ERR_FLG
            ENDTRY
            PROCEDURE Changed()
                PRINT COM_ERR_FLG
                COM_ERR_IGN
                COM_ERR_RET
                EVENT_PRM[1]=TRUE
            FEND
            """, output.Add);
        Assert.Equal(["-999999", "1", "1", "0", "1"], output);
    }

    [Fact]
    public async Task ModuleProcedureWithOptionalParameterIsResolvedAtRegistration()
    {
        List<string> output = [];
        await new Interpreter(windows: new Automation()).RunAsync("""
            DIM obj=CREATEOLEOBJ("fixture")
            Handler.Register(obj)
            PRINT obj.Fire(42)
            MODULE Handler
                PROCEDURE Register(obj)
                    OLEEVENT(obj,"Events","Changed","Changed")
                FEND
                PROCEDURE Changed(value=9)
                    PRINT value
                    EVENT_PRM[1]=TRUE
                FEND
            ENDMODULE
            """, output.Add);
        Assert.Equal(["9", "1"], output);
    }

    [Theory]
    [InlineData("obj,\"Events\"")]
    [InlineData("obj,\"Events\",\"Changed\"")]
    [InlineData("obj,\"Events\",\"Changed\",\"Changed\",1")]
    [InlineData("obj,,\"Changed\",\"Changed\"")]
    [InlineData("obj,\"Events\",\"Changed\",\"Missing\"")]
    [InlineData("obj,\"Events\",\"Changed\",\"Required\"")]
    [InlineData("obj,\"Events\",\"Changed\",\"FunctionTarget\"")]
    [InlineData("obj,\"\",\"Changed\",\"Changed\"")]
    [InlineData("1,\"Events\",\"Changed\",\"Changed\"")]
    public async Task InvalidRegistrationDoesNotReachProvider(string arguments)
    {
        var automation = new Automation();
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync($$"""
            DIM obj=CREATEOLEOBJ("fixture")
            TRY
                OLEEVENT({{arguments}})
            EXCEPT
                PRINT "rejected"
            ENDTRY
            PROCEDURE Changed()
            FEND
            PROCEDURE Required(value)
            FEND
            FUNCTION FunctionTarget()
                RESULT=1
            FEND
            """, output.Add);
        Assert.Equal(["rejected"], output);
        Assert.Equal(0, automation.Registrations);
    }
}
