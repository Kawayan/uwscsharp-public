using System.Collections.Concurrent;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputEmergencyBridgeTests
{
    [Fact]
    public async Task EmergencyStopsRealInterpreterRootAndThreadTogether()
    {
        var registry = new SelectiveInputEmergencyRegistry();
        var hook = new Hook();
        IWindowsAutomationSession root = Create(registry, hook).CreateSession();
        await ((ISelectiveInputOwnershipSession)root).SetSelectiveInputAsync(new(0, 0), default);
        var host = new Host();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task? run = null;
        try
        {
            run = new Interpreter(windows: new Automation(root)).RunAsync("""
                OPTION OPTFINALLY
                THREAD Worker()
                TRY
                    SLEEP(99)
                FINALLY
                    PRINT "root-finally"
                ENDTRY
                PROCEDURE Worker()
                    TRY
                        SLEEP(99)
                    FINALLY
                        PRINT "worker-finally"
                    ENDTRY
                FEND
                """, host, caller.Token);
            await host.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            registry.RequestAll();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(["root-finally", "worker-finally"], host.Output.Order());
            Assert.Equal(1, hook.Stops);
            Assert.False(hook.IsRegistered);
        }
        finally
        {
            caller.Cancel();
            if (run is not null)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch { }
            }
            await root.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EmergencyCancelsTheRunExecutesFinallyAndRejectsReactivation(int mode)
    {
        var registry = new SelectiveInputEmergencyRegistry();
        var hook = new Hook();
        IWindowsAutomationSession root = Create(registry, hook).CreateSession();
        ISelectiveInputOwnershipSession selective = Assert.IsAssignableFrom<ISelectiveInputOwnershipSession>(root);
        await selective.SetSelectiveInputAsync(new(0, mode), default);
        IWindowsAutomationSession worker = root.Fork();
        Assert.Equal(root.ExecutionStopToken, worker.ExecutionStopToken);
        await worker.DisposeAsync();
        Assert.False(root.ExecutionStopToken.IsCancellationRequested);
        var host = new Host();
        Exception? reactivation = null;
        host.OnOutput = async text =>
        {
            if (text == "finally")
            {
                reactivation = await Record.ExceptionAsync(() =>
                    selective.SetSelectiveInputAsync(new(0, mode), CancellationToken.None));
                await selective.SetSelectiveInputAsync(new(null, 0), CancellationToken.None);
            }
        };
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task? run = null;
        try
        {
            run = new Interpreter(windows: new Automation(root)).RunAsync("""
            OPTION OPTFINALLY
            TRY
                TRY
                    SLEEP(99)
                EXCEPT
                    PRINT "caught"
                ENDTRY
            FINALLY
                PRINT "finally"
            ENDTRY
            """, host, caller.Token);
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            registry.RequestAll();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(root.ExecutionStopToken, error.CancellationToken);
            Assert.Equal(["finally"], host.Output);
            Assert.Contains("緊急解除", Assert.IsType<InvalidOperationException>(reactivation).Message);
            Assert.False(hook.IsRegistered);
            Assert.True(hook.Suspended);
            Assert.Equal(1, hook.Stops);
        }
        finally
        {
            caller.Cancel();
            if (run is not null)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // 本文で検査する実行結果を変えず、後始末を続けます。
                }
            }

            await root.DisposeAsync();
        }
    }

    [Fact]
    public async Task RegistryStopsAllTargetRunsAndLeavesAnUntargetedRunAlive()
    {
        var registry = new SelectiveInputEmergencyRegistry();
        var first = new Hook();
        var second = new Hook();
        IWindowsAutomationSession[] roots =
            [Create(registry, first).CreateSession(), Create(registry, second).CreateSession(),
             Create(registry, new Hook()).CreateSession()];
        await ((ISelectiveInputOwnershipSession)roots[0]).SetSelectiveInputAsync(new(0, 1), default);
        await ((ISelectiveInputOwnershipSession)roots[1]).SetSelectiveInputAsync(new(0, 2), default);
        Host[] hosts = [new(), new(), new()];
        using var stopIdle = new CancellationTokenSource();
        Task[] runs = [.. roots.Select((root, index) =>
            new Interpreter(windows: new Automation(root)).RunAsync("SLEEP(99)", hosts[index],
                index == 2 ? stopIdle.Token : default))];
        try
        {
            await Task.WhenAll(hosts.Select(host => host.Started.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            registry.RequestAll();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runs[0]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runs[1]);
            Assert.False(runs[2].IsCompleted);
            Assert.False(roots[2].ExecutionStopToken.IsCancellationRequested);
            Assert.False(first.IsRegistered);
            Assert.False(second.IsRegistered);
        }
        finally
        {
            stopIdle.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runs[2]);
            foreach (IWindowsAutomationSession root in roots)
            {
                await root.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task EndedRetainedOwnerIsRecoveredWithoutChangingTheOriginalFailure()
    {
        var registry = new SelectiveInputEmergencyRegistry();
        var hook = new Hook { StopFailures = 2 };
        WindowsAutomation automation = Create(registry, hook);
        IWindowsAutomationSession root = automation.CreateSession();
        await ((ISelectiveInputOwnershipSession)root).SetSelectiveInputAsync(new(0, 0), default);
        Task<int> run = new Interpreter(windows: new Automation(root)).RunWithExitCodeAsync(
            "EXITEXIT(37)", new CallbackScriptHost(_ => { }), default, ScriptRunOptions.Empty);
        await Assert.ThrowsAnyAsync<Exception>(() => run);
        Exception original = Assert.Single(run.Exception!.InnerExceptions);
        Assert.Equal(1, automation.SelectiveInputCleanup.PendingCount);
        Assert.False(root.ExecutionStopToken.IsCancellationRequested);
        registry.RequestAll();
        await SelectiveInputEmergencyStopTests.WaitAsync(() =>
            automation.SelectiveInputCleanup.PendingCount == 0);
        Assert.False(root.ExecutionStopToken.IsCancellationRequested);
        Assert.Same(original, Assert.Single(run.Exception!.InnerExceptions));
        Assert.False(hook.IsRegistered);
        Assert.Equal(3, hook.Stops);
    }

    [Fact]
    public async Task CancellationCallbackFailuresRemainInCleanupDiagnostics()
    {
        var registry = new SelectiveInputEmergencyRegistry();
        var hook = new Hook();
        IWindowsAutomationSession root = Create(registry, hook).CreateSession();
        await ((ISelectiveInputOwnershipSession)root).SetSelectiveInputAsync(new(0, 0), default);
        var failure = new InvalidOperationException("検証用の取消callback失敗");
        using CancellationTokenRegistration registration = root.ExecutionStopToken.Register(() => throw failure);
        var host = new Host();
        Task run = new Interpreter(windows: new Automation(root)).RunAsync("SLEEP(99)", host);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        registry.RequestAll();
        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        AggregateException errors = Assert.IsType<AggregateException>(cancelled.Data["UWSCSharp.CleanupErrors"]);
        Assert.Contains(failure, errors.Flatten().InnerExceptions);
        Assert.False(hook.IsRegistered);
    }

    private static WindowsAutomation Create(SelectiveInputEmergencyRegistry registry, Hook hook) =>
        new(_ => throw new InvalidOperationException("入力送信を使用しません。"),
            swappedMouseButtons: false, keyStateBackend: new Keys(),
            selectiveInputFactory: (_, _) => hook)
        {
            EmergencyRegistry = registry
        };

    private sealed class Automation(IWindowsAutomationSession root) : IWindowsAutomation
    {
        public IWindowsAutomationSession CreateSession() => root;
    }

    private sealed class Hook : ISelectiveInputHookSession
    {
        private int registered;
        internal int Stops;
        internal int StopFailures;
        internal bool Suspended;
        internal SelectiveInputEmergencyStop? Emergency;
        public bool IsRegistered => Volatile.Read(ref registered) != 0;
        public void ConfigureEmergency(SelectiveInputEmergencyStop emergency) => Emergency = emergency;
        public void Start() => Volatile.Write(ref registered, 1);
        public bool Pump() => false;
        public void Wait(AutoResetEvent work, int milliseconds) => work.WaitOne(milliseconds);
        public void RefreshTargets()
        {
        }
        public void Suspend() => Suspended = true;
        public void Stop()
        {
            Interlocked.Increment(ref Stops);
            if (Interlocked.Decrement(ref StopFailures) >= 0)
            {
                throw new InvalidOperationException("検証用の解除失敗");
            }

            Volatile.Write(ref registered, 0);
        }
    }

    private sealed class Keys : IKeyStateBackend
    {
        public short ReadAsync(int key) => 0;
        public short ReadToggle(int key) => 0;
        public bool ReadIme(CancellationToken token) => false;
    }

    private sealed class Host : IScriptHost
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource BothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int delays;
        internal readonly ConcurrentQueue<string> Output = new();
        internal Func<string, Task>? OnOutput;
        public async ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            Output.Enqueue(text);
            if (OnOutput is not null)
            {
                await OnOutput(text);
            }
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            Started.TrySetResult();
            if (Interlocked.Increment(ref delays) == 2)
            {
                BothStarted.TrySetResult();
            }
            return Task.Delay(Timeout.Infinite, token);
        }
    }
}
