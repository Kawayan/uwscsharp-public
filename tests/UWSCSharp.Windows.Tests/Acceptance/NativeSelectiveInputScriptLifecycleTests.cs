using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputScriptLifecycleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, "normal")]
    [InlineData(0, "cancel")]
    [InlineData(0, "error")]
    [InlineData(1, "normal")]
    [InlineData(1, "cancel")]
    [InlineData(1, "error")]
    [InlineData(2, "normal")]
    [InlineData(2, "cancel")]
    [InlineData(2, "error")]
    public async Task PublicThreadSharesReplacesReleasesAndRecoversRealHooks(
        int mode, string ending)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        var backend = new Backend();
        var observation = new Observation();
        IWindowsAutomationSession native = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力送信は使用しません。"),
            swappedMouseButtons: false, selectiveInputFactory: (target, devices) =>
            {
                backend.Targets.Enqueue(target.Handle);
                return new NativeSelectiveInputHookSession(target, devices, backend);
            }).CreateSession();
        IWindowsAutomationSession root = new ObservedSession(native, observation, true);
        var host = new Host();
        using var caller = new CancellationTokenSource();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller.Token, watchdog.Token);
        int replacement = (mode + 1) % 3;
        string handle = ((long)fixture.Root).ToString(CultureInfo.InvariantCulture);
        string source = $$"""
            THREAD Worker()
            PRINT "root-held"
            LOCKHARDEX(HNDTOID({{handle}}),{{replacement}})
            PRINT "replaced"
            LOCKHARDEX()
            PRINT "released"
            LOCKHARDEX(0,{{mode}})
            PRINT "global"
            {{(ending == "error" ? "PRINT INT(NULL)" : "")}}
            PROCEDURE Worker()
                DIM id=HNDTOID({{handle}})
                LOCKHARDEX(id,{{mode}})
                LOCKHARDEX(id,{{mode}})
            FEND
            """;
        Task? run = null;
        var samples = new List<object>();
        try
        {
            run = new Interpreter(windows: new Automation(root)).RunAsync(source, host, linked.Token);
            await host.ArrivedAsync("root-held");
            await observation.ChildDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, observation.Forks);
            Assert.Equal(1, observation.ChildDisposals);
            Assert.Equal(0, observation.RootDisposals);
            Observe("child-disposed", Count(mode), Count(mode), 0);

            host.Release("root-held");
            await host.ArrivedAsync("replaced");
            Observe("replaced", Count(replacement), Count(mode) + Count(replacement), Count(mode));
            host.Release("replaced");
            await host.ArrivedAsync("released");
            Observe("released", 0, Count(mode) + Count(replacement), Count(mode) + Count(replacement));
            host.Release("released");
            await host.ArrivedAsync("global");
            int installs = 2 * Count(mode) + Count(replacement);
            Observe("global", Count(mode), installs, Count(mode) + Count(replacement));
            if (ending == "cancel")
            {
                await caller.CancelAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                host.Release("global");
                if (ending == "error")
                {
                    ScriptException error = await Assert.ThrowsAsync<ScriptException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
                    Assert.Equal(9, error.Line);
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
            Observe("root-ended", 0, installs, installs);
            Assert.Equal([fixture.Root, fixture.Root, 0], [.. backend.Targets]);
            Assert.Single(backend.Threads.Distinct());
            output.WriteLine(JsonSerializer.Serialize(new
            {
                mode,
                replacement,
                ending,
                child_disposals = observation.ChildDisposals,
                root_disposals = observation.RootDisposals,
                forks = observation.Forks,
                owner_threads = backend.Threads.Distinct().Count(),
                samples,
                limits = "実Interpreter・実フックを使う。入力配送・物理キー・実OS障害を検証するものではない。"
            }));
        }
        finally
        {
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
                        // 本体の失敗を保持し、待機期限後も実登録の回収へ進みます。
                    }
                }
            }
            finally
            {
                host.ReleaseAll();
                await root.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, Volatile.Read(ref backend.Active));
            }
        }

        void Observe(string phase, int active, int installs, int removals)
        {
            Assert.Equal(active, Volatile.Read(ref backend.Active));
            Assert.Equal(installs, Volatile.Read(ref backend.Installs));
            Assert.Equal(removals, Volatile.Read(ref backend.Removals));
            samples.Add(new
            {
                phase,
                active,
                installs,
                removals
            });
        }
    }

    private static int Count(int mode) => mode == 1 ? 1 : 2;

    private sealed class Backend : ISelectiveInputHookBackend
    {
        private readonly NativeSelectiveInputHookBackend native = new();
        internal readonly ConcurrentQueue<nint> Targets = new();
        internal readonly ConcurrentQueue<int> Threads = new();
        internal int Active;
        internal int Installs;
        internal int Removals;

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
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
            (bool success, int error) = native.Remove(hook);
            if (success)
            {
                Interlocked.Decrement(ref Active);
                Interlocked.Increment(ref Removals);
            }
            return (success, error);
        }
    }

    private sealed class Host : IScriptHost
    {
        private readonly Dictionary<string, Gate> gates = new()
        {
            ["root-held"] = new(),
            ["replaced"] = new(),
            ["released"] = new(),
            ["global"] = new()
        };

        public async ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (gates.TryGetValue(text, out Gate? gate))
            {
                gate.Arrived.TrySetResult();
                await gate.Continue.Task.WaitAsync(token);
            }
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token) => Task.Delay(duration, token);

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

    private sealed class Gate
    {
        internal readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Observation
    {
        internal readonly TaskCompletionSource ChildDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Forks;
        internal int ChildDisposals;
        internal int RootDisposals;
    }

    private sealed class Automation(IWindowsAutomationSession root) : IWindowsAutomation
    {
        public IWindowsAutomationSession CreateSession() => root;
    }

    // 観測を足しても、実セッションの停止・障害・公開呼出しを既定実装へ落としません。
    private sealed class ObservedSession(IWindowsAutomationSession inner, Observation observation, bool root)
        : ForwardingWindowsAutomationSession(inner)
    {
        private int disposed;

        public override IWindowsAutomationSession Fork()
        {
            IWindowsAutomationSession child = Inner.Fork();
            Assert.Equal(Inner.ExecutionStopToken, child.ExecutionStopToken);
            Interlocked.Increment(ref observation.Forks);
            return new ObservedSession(child, observation, false);
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
                if (root)
                {
                    Interlocked.Increment(ref observation.RootDisposals);
                }
                else
                {
                    Interlocked.Increment(ref observation.ChildDisposals);
                    observation.ChildDisposed.TrySetResult();
                }
            }
            catch (Exception error)
            {
                if (!root)
                {
                    observation.ChildDisposed.TrySetException(error);
                }
                throw;
            }
        }
    }
}
