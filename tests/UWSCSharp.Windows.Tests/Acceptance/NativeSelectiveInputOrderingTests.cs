using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputOrderingTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool global in new[] { false, true })
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                foreach (string scenario in new[]
                {
                    "release-overtakes", "register-after-release", "normal-end",
                    "error-end", "cancel-end", "normal-end-failure"
                })
                {
                    yield return [global, mode, scenario];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PublicRequestsRespectReleaseAndExecutionEnd(
        bool global, int mode, string scenario)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        bool releasing = scenario == "register-after-release";
        bool ending = scenario.EndsWith("-end", StringComparison.Ordinal)
            || scenario == "normal-end-failure";
        using var backend = new Backend(releasing);
        var observation = new Observation();
        var cleanup = new WindowsSelectiveInputCleanup();
        var host = new Host();
        IWindowsAutomationSession native = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力送信は使用しません。"),
            swappedMouseButtons: false, selectiveInputFactory: (target, devices) =>
            {
                Assert.Equal(global ? 0 : fixture.Root, target.Handle);
                Assert.Equal(0, Volatile.Read(ref backend.Active));
                Interlocked.Increment(ref backend.Sessions);
                return new NativeSelectiveInputHookSession(target, devices, backend);
            })
        {
            SelectiveInputCleanup = cleanup
        }.CreateSession();
        IWindowsAutomationSession root = new Session(native, observation, true);
        using var caller = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, watchdog.Token);
        string handle = ((long)fixture.Root).ToString(CultureInfo.InvariantCulture);
        int workers = scenario == "release-overtakes" ? 4 : 2;
        string source = $$"""
            PUBLIC target={{(global ? "0" : $"HNDTOID({handle})")}}
            {{(releasing ? $"LOCKHARDEX(target,{mode})" : "")}}
            THREAD Worker(1)
            THREAD Worker(2)
            {{(workers == 4 ? "THREAD Worker(3)\nTHREAD Worker(4)" : "")}}
            PRINT "root-held"
            {{(scenario == "error-end" ? "PRINT INT(NULL)" : "")}}
            PROCEDURE Worker(n)
                SLEEP(n/1000)
                TRY
                    IF {{(releasing ? "n=1" : "n=3")}} THEN
                        LOCKHARDEX()
                    ELSE
                        LOCKHARDEX(target,{{mode}})
                    ENDIF
                EXCEPT
                    DIM ignored=1
                ENDTRY
            FEND
            """;
        Task? run = null;
        int pending = 0;
        string? lateRejection = null;
        try
        {
            run = new Interpreter(windows: new Automation(root)).RunAsync(source, host, linked.Token);
            await StageAsync(host.ArrivedAsync("root-held"));
            await StageAsync(host.ArrivedAsync("ready-1"));
            host.Release("ready-1");
            await StageAsync(backend.Entered.Task);
            await StageAsync(observation.Submitted[0].Task);
            for (int worker = 2; worker <= workers; worker++)
            {
                await StageAsync(host.ArrivedAsync("ready-" + worker));
                host.Release("ready-" + worker);
                await StageAsync(observation.Submitted[worker - 1].Task);
            }
            Task<Value>[] requests = [.. observation.Requests];
            pending = requests.Count(task => !task.IsCompleted);
            Assert.Equal(workers, requests.Length);
            Assert.Equal(workers, pending);
            Assert.False(run.IsCompleted);

            if (ending)
            {
                backend.FailRemoval = scenario == "normal-end-failure";
                if (scenario == "cancel-end")
                {
                    await caller.CancelAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    host.Release("root-held");
                }
                await observation.RootCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(run.IsCompleted);
                IWindowsAutomationSession child = observation.Child!;
                Exception? rejected = await Record.ExceptionAsync(() =>
                    ((ISelectiveInputOwnershipSession)child).SetSelectiveInputAsync(new(0, mode), default));
                Assert.True(rejected is OperationCanceledException or ObjectDisposedException,
                    "終了要求後の設定は取消または終了済みとして拒否される必要があります。");
                lateRejection = rejected!.GetType().Name;
            }

            backend.Resume.Set();
            if (!ending)
            {
                if (releasing)
                {
                    Assert.All(await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5)),
                        value => Assert.Equal(ValueKind.Empty, value.Kind));
                }
                else
                {
                    InvalidOperationException first = await Assert.ThrowsAsync<InvalidOperationException>(() => requests[0]);
                    InvalidOperationException second = await Assert.ThrowsAsync<InvalidOperationException>(() => requests[1]);
                    Assert.IsType<SelectiveInputSupersededException>(first.InnerException);
                    Assert.IsType<SelectiveInputSupersededException>(second.InnerException);
                    Assert.Equal(ValueKind.Empty, (await requests[2]).Kind);
                    Assert.Equal(ValueKind.Empty, (await requests[3]).Kind);
                }
                Assert.Equal(2, backend.Sessions);
                Assert.Equal(Count(mode), Volatile.Read(ref backend.Active));
                Assert.Equal(2 * Count(mode), backend.Installs);
                Assert.Equal(Count(mode), backend.Removals);
                await observation.ChildrenDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
                host.Release("root-held");
            }

            if (scenario == "normal-end-failure")
            {
                Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
                    run.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(HasInjectedFailure(failure), failure.ToString());
                Assert.Equal(1, cleanup.PendingCount);
                Assert.Equal(Count(mode), Volatile.Read(ref backend.Active));
                Assert.Equal(1, backend.Sessions);
                Assert.True(backend.RejectedRemovals > 0);
                backend.FailRemoval = false;
                WindowsSelectiveInputCleanupResult recovered = await cleanup.RetryAsync();
                Assert.Equal(0, recovered.PendingCount);
            }
            else if (scenario == "error-end")
            {
                ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                    run.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Contains("NULL", error.Message);
            }
            else if (scenario == "cancel-end")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    run.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                await run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.False(watchdog.IsCancellationRequested);
            Assert.Equal(0, cleanup.PendingCount);
            Assert.Equal(0, Volatile.Read(ref backend.Active));
            Assert.Equal(backend.Installs, backend.Removals);
            Assert.Equal(ending ? 1 : 2, backend.Sessions);
            Assert.Single(backend.Threads.Distinct());
            Assert.Equal(workers, observation.ChildDisposals);
            Assert.Equal(1, observation.RootDisposals);
            if (ending)
            {
                Assert.All(requests, request => Assert.True(request.IsCanceled || request.IsFaulted));
                Assert.Equal(Count(mode), backend.Installs);
            }
            output.WriteLine(JsonSerializer.Serialize(new
            {
                global,
                mode,
                scenario,
                pending_before_resume = pending,
                sessions = backend.Sessions,
                installs = backend.Installs,
                removals = backend.Removals,
                active = backend.Active,
                rejected_removals = backend.RejectedRemovals,
                owner_threads = backend.Threads.Distinct().Count(),
                request_outcomes = requests.Select(request => request.Status.ToString()).ToArray(),
                child_disposals = observation.ChildDisposals,
                root_disposals = observation.RootDisposals,
                cleanup_pending = cleanup.PendingCount,
                late_rejection = lateRejection
            }));
        }
        finally
        {
            backend.FailRemoval = false;
            backend.Resume.Set();
            host.ReleaseAll();
            try
            {
                await caller.CancelAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (run is not null)
                {
                    try
                    {
                        await run.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    catch
                    {
                        // 本体の失敗を保持し、検証が所有する実フックの回収へ進みます。
                    }
                }
            }
            finally
            {
                try
                {
                    await root.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    await cleanup.RetryAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        final_cleanup = true,
                        global,
                        mode,
                        scenario,
                        pending,
                        active = backend.Active,
                        pending_owners = cleanup.PendingCount,
                        run_error = run?.Exception?.ToString()
                    }));
                    Assert.Equal(0, backend.Active);
                }
            }
        }

        async Task StageAsync(Task signal)
        {
            Task completed = await Task.WhenAny(signal, run!).WaitAsync(TimeSpan.FromSeconds(5));
            if (completed == run)
            {
                await run!;
                Assert.Fail("準備通知の前にスクリプトが終了しました。");
            }
            await signal;
        }
    }

    private static int Count(int mode) => mode == 1 ? 1 : 2;

    private static bool HasInjectedFailure(Exception failure)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(failure);
        while (pending.TryPop(out Exception? current))
        {
            if (!visited.Add(current))
            {
                continue;
            }
            if (current is Win32Exception { NativeErrorCode: 5 })
            {
                return true;
            }
            if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
            if (current is AggregateException aggregate)
            {
                foreach (Exception error in aggregate.InnerExceptions)
                {
                    pending.Push(error);
                }
            }
            foreach (Exception error in current.Data.Values.OfType<Exception>())
            {
                pending.Push(error);
            }
        }
        return false;
    }

    private sealed class Backend(bool pauseRemoval) : ISelectiveInputHookBackend, IDisposable
    {
        private readonly NativeSelectiveInputHookBackend native = new();
        private int paused;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Resume = new();
        internal readonly ConcurrentQueue<int> Threads = new();
        internal volatile bool FailRemoval;
        internal int Active;
        internal int Installs;
        internal int Removals;
        internal int RejectedRemovals;
        internal int Sessions;

        private void Pause(bool removal)
        {
            if (removal == pauseRemoval && Interlocked.Exchange(ref paused, 1) == 0)
            {
                Entered.TrySetResult();
                Assert.True(Resume.Wait(TimeSpan.FromSeconds(5)), "競合条件の受付確認が期限を超えました。");
            }
        }

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            Pause(false);
            (nint handle, int error) = native.Install(type, procedure);
            if (handle != 0)
            {
                Interlocked.Increment(ref Active);
                Interlocked.Increment(ref Installs);
            }
            return (handle, error);
        }

        public (bool Success, int Error) Remove(nint hook)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            Pause(true);
            if (FailRemoval)
            {
                Interlocked.Increment(ref RejectedRemovals);
                return (false, 5);
            }
            (bool success, int error) = native.Remove(hook);
            if (success)
            {
                Interlocked.Decrement(ref Active);
                Interlocked.Increment(ref Removals);
            }
            return (success, error);
        }

        public void Dispose() => Resume.Dispose();
    }

    private sealed class Host : IScriptHost
    {
        private sealed class Gate
        {
            internal readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly Dictionary<string, Gate> gates = new()
        {
            ["root-held"] = new(),
            ["ready-1"] = new(),
            ["ready-2"] = new(),
            ["ready-3"] = new(),
            ["ready-4"] = new()
        };

        public async ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            if (gates.TryGetValue(text, out Gate? gate))
            {
                gate.Arrived.TrySetResult();
                await gate.Continue.Task.WaitAsync(token);
            }
        }

        public async Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            int worker = checked((int)Math.Round(duration.TotalMilliseconds));
            Assert.InRange(worker, 1, 4);
            Assert.Equal(TimeSpan.FromMilliseconds(worker), duration);
            Gate gate = gates["ready-" + worker];
            gate.Arrived.TrySetResult();
            await gate.Continue.Task.WaitAsync(token);
        }

        internal Task ArrivedAsync(string name) => gates[name].Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        internal void Release(string name) => gates[name].Continue.TrySetResult();

        internal void ReleaseAll()
        {
            foreach (Gate gate in gates.Values)
            {
                gate.Continue.TrySetResult();
            }
        }
    }

    private sealed class Observation
    {
        internal readonly ConcurrentQueue<Task<Value>> Requests = new();
        internal readonly TaskCompletionSource[] Submitted = [.. Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))];
        internal readonly TaskCompletionSource ChildrenDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RootCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IWindowsAutomationSession? Child;
        internal int ChildDisposals;
        internal int Forks;
        internal int RootDisposals;
    }

    private sealed class Automation(IWindowsAutomationSession root) : IWindowsAutomation
    {
        public IWindowsAutomationSession CreateSession() => root;
    }

    private sealed class Session(IWindowsAutomationSession inner, Observation observation, bool root)
        : ForwardingWindowsAutomationSession(inner)
    {
        private int disposed;

        public override IWindowsAutomationSession Fork()
        {
            IWindowsAutomationSession child = Inner.Fork();
            observation.Child = child;
            Interlocked.Increment(ref observation.Forks);
            return new Session(child, observation, false);
        }

        public override void ConfigurePause(ScriptPauseControl? control, CancellationToken token)
        {
            Inner.ConfigurePause(control, token);
            if (root)
            {
                token.UnsafeRegister(_ => observation.RootCancelled.TrySetResult(), null);
            }
        }

        public override async ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Task<Value> request = Inner.InvokeAsync(name, arguments, token).AsTask();
            if (!root && name.Equals(BuiltinFunctionNames.LockHardEx, StringComparison.OrdinalIgnoreCase))
            {
                observation.Requests.Enqueue(request);
                observation.Submitted[observation.Requests.Count - 1].TrySetResult();
            }
            return await request;
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            try
            {
                await Inner.DisposeAsync();
            }
            finally
            {
                if (root)
                {
                    Interlocked.Increment(ref observation.RootDisposals);
                }
                else if (Interlocked.Increment(ref observation.ChildDisposals) == observation.Forks)
                {
                    observation.ChildrenDone.TrySetResult();
                }
            }
        }
    }
}
