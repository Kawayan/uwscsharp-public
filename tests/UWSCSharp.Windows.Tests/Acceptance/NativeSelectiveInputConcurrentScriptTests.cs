using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputConcurrentScriptTests(ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ThreeChildrenOverlapPublicRequestsAndShareTheWinner(
        int pattern, string ending)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        using var backend = new Backend();
        var observation = new Observation();
        var host = new Host();
        IWindowsAutomationSession native = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力送信は使用しません。"),
            swappedMouseButtons: false, selectiveInputFactory: (target, devices) =>
            {
                Assert.Equal(0, Volatile.Read(ref backend.Active));
                Assert.Equal(fixture.Root, target.Handle);
                backend.Modes.Enqueue(devices == SelectiveInputDevices.All ? 0 : (int)devices);
                return new NativeSelectiveInputHookSession(target, devices, backend);
            }).CreateSession();
        IWindowsAutomationSession root = new Session(native, observation, true);
        using var caller = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, watchdog.Token);
        int[] requested = pattern == 3 ? [0, 1, 2] : [pattern, pattern, pattern];
        string handle = ((long)fixture.Root).ToString(CultureInfo.InvariantCulture);
        string source = $$"""
            PUBLIC target=HNDTOID({{handle}})
            THREAD Worker({{requested[0]}})
            THREAD Worker({{requested[1]}})
            THREAD Worker({{requested[2]}})
            PRINT "root-held"
            {{(ending == "error" ? "PRINT INT(NULL)" : "")}}
            PROCEDURE Worker(m)
                SLEEP(0.001)
                LOCKHARDEX(target,m)
            FEND
            """;
        Task? run = null;
        try
        {
            run = new Interpreter(windows: new Automation(root)).RunAsync(source, host, linked.Token);
            await host.RootHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.ChildrenReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.StartChildren.TrySetResult();
            await backend.FirstInstall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await observation.AllSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<Value>[] pending = [.. observation.Requests];
            Assert.Equal(3, pending.Length);
            int pendingAtRelease = pending.Count(task => !task.IsCompleted);
            Assert.Equal(3, pendingAtRelease);
            Assert.Equal(0, observation.Completed);
            backend.ContinueInstall.Set();
            await observation.ChildrenDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(3, observation.Completed);
            Assert.Equal(3, observation.ChildDisposals);
            Assert.Equal(3, observation.Forks);
            Assert.Equal(0, observation.RootDisposals);
            Assert.All(await Task.WhenAll(pending), value => Assert.Equal(ValueKind.Empty, value.Kind));

            int[] selected = [.. backend.Modes];
            if (pattern == 3)
            {
                Assert.Equal([0, 1, 2], selected.Order());
            }
            else
            {
                Assert.Equal([pattern], selected);
            }
            int winner = selected[^1];
            int installed = selected.Sum(Count);
            Assert.Equal(installed, backend.Installs);
            Assert.Equal(Count(winner), backend.Active);
            Assert.Equal(installed - Count(winner), backend.Removals);
            Assert.InRange(backend.Peak, 1, 2);
            Assert.Single(backend.Threads.Distinct());

            if (ending == "cancel")
            {
                await caller.CancelAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                host.ReleaseRoot.TrySetResult();
                if (ending == "error")
                {
                    ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.Equal(6, error.Line);
                    Assert.Contains("NULL", error.Message);
                }
                else
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            Assert.False(watchdog.IsCancellationRequested);
            Assert.Equal(ending == "cancel", caller.IsCancellationRequested);
            Assert.Equal(1, observation.RootDisposals);
            Assert.Equal(0, backend.Active);
            Assert.Equal(installed, backend.Removals);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                pattern,
                ending,
                requested,
                selected,
                winner,
                pending_at_release = pendingAtRelease,
                child_disposals = observation.ChildDisposals,
                root_disposals = observation.RootDisposals,
                completed = observation.Completed,
                installs = backend.Installs,
                removals = backend.Removals,
                active = backend.Active,
                peak = backend.Peak,
                owner_threads = backend.Threads.Distinct().Count()
            }));
        }
        finally
        {
            backend.ContinueInstall.Set();
            host.StartChildren.TrySetResult();
            host.ReleaseRoot.TrySetResult();
            try
            {
                await caller.CancelAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (run is not null)
                {
                    try
                    {
                        await run.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch
                    {
                        // 本体の障害を保持して実登録の回収へ進みます。
                    }
                }
            }
            finally
            {
                await root.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, Volatile.Read(ref backend.Active));
            }
        }
    }

    public static IEnumerable<object[]> Cases
    {
        get
        {
            foreach (int pattern in new[] { 0, 1, 2, 3 })
            {
                foreach (string ending in new[] { "normal", "cancel", "error" })
                {
                    yield return [pattern, ending];
                }
            }
        }
    }

    private static int Count(int mode) => mode == 1 ? 1 : 2;

    private sealed class Backend : ISelectiveInputHookBackend, IDisposable
    {
        private readonly NativeSelectiveInputHookBackend native = new();
        internal readonly TaskCompletionSource FirstInstall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim ContinueInstall = new();
        internal readonly ConcurrentQueue<int> Modes = new();
        internal readonly ConcurrentQueue<int> Threads = new();
        internal int Active;
        internal int Installs;
        internal int Removals;
        internal int Peak;

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            if (!FirstInstall.Task.IsCompleted)
            {
                FirstInstall.TrySetResult();
                Assert.True(ContinueInstall.Wait(TimeSpan.FromSeconds(5)), "同時要求の確認待ちが終了しません。");
            }
            (nint handle, int error) = native.Install(type, procedure);
            if (handle != 0)
            {
                Interlocked.Increment(ref Installs);
                int active = Interlocked.Increment(ref Active);
                Peak = Math.Max(Peak, active);
            }
            return (handle, error);
        }

        public (bool Success, int Error) Remove(nint hook)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            (bool success, int error) = native.Remove(hook);
            if (success)
            {
                Interlocked.Increment(ref Removals);
                Interlocked.Decrement(ref Active);
            }
            return (success, error);
        }

        public void Dispose() => ContinueInstall.Dispose();
    }

    private sealed class Host : IScriptHost
    {
        internal readonly TaskCompletionSource RootHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseRoot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ChildrenReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource StartChildren = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int ready;

        public async ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            Assert.Equal("root-held", text);
            RootHeld.TrySetResult();
            await ReleaseRoot.Task.WaitAsync(token);
        }

        public async Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            if (Interlocked.Increment(ref ready) == 3)
            {
                ChildrenReady.TrySetResult();
            }
            await StartChildren.Task.WaitAsync(token);
        }
    }

    private sealed class Observation
    {
        internal readonly ConcurrentQueue<Task<Value>> Requests = new();
        internal readonly TaskCompletionSource AllSubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ChildrenDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Forks;
        internal int Completed;
        internal int ChildDisposals;
        internal int RootDisposals;
    }

    private sealed class Automation(IWindowsAutomationSession root) : IWindowsAutomation
    {
        public IWindowsAutomationSession CreateSession() => root;
    }

    private sealed class Session(IWindowsAutomationSession inner,
        Observation observation, bool root) : ForwardingWindowsAutomationSession(inner)
    {
        private int disposed;

        public override IWindowsAutomationSession Fork()
        {
            IWindowsAutomationSession child = Inner.Fork();
            Assert.Equal(ExecutionStopToken, child.ExecutionStopToken);
            Interlocked.Increment(ref observation.Forks);
            return new Session(child, observation, false);
        }

        public override async ValueTask<Value> InvokeAsync(string name,
            IReadOnlyList<Value> arguments, CancellationToken token)
        {
            Task<Value> pending = Inner.InvokeAsync(name, arguments, token).AsTask();
            bool selected = !root && name.Equals(BuiltinFunctionNames.LockHardEx, StringComparison.OrdinalIgnoreCase);
            if (selected)
            {
                observation.Requests.Enqueue(pending);
                if (observation.Requests.Count == 3)
                {
                    observation.AllSubmitted.TrySetResult();
                }
            }
            Value result = await pending;
            if (selected)
            {
                Interlocked.Increment(ref observation.Completed);
            }
            return result;
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            await Inner.DisposeAsync();
            if (root)
            {
                Interlocked.Increment(ref observation.RootDisposals);
            }
            else if (Interlocked.Increment(ref observation.ChildDisposals) == 3)
            {
                observation.ChildrenDisposed.TrySetResult();
            }
        }
    }
}
