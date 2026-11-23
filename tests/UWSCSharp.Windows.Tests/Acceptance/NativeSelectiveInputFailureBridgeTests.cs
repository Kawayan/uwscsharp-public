using System.Globalization;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputFailureBridgeTests(ITestOutputHelper output)
{
    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task PublicTargetFailureCancelsInterpreterWaitAndReleasesRealHook(
        string failure, bool handled, int mode)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            _ =>
            {
            });
        var platform = new Platform(failure);
        var backend = new Backend();
        IWindowsAutomationSession root = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力送信を使用しません。"),
            swappedMouseButtons: false, selectiveInputFactory: (target, devices) =>
                new NativeSelectiveInputHookSession(target, devices, backend),
            selectiveInputPlatform: platform).CreateSession();
        var host = new Host();
        using var stop = new CancellationTokenSource();
        string body = handled
            ? "TRY\n SLEEP(99)\nEXCEPT\n PRINT \"caught\"\nENDTRY\nPRINT \"alive\""
            : "SLEEP(99)";
        string handle = ((long)fixture.Root).ToString(CultureInfo.InvariantCulture);
        string source = $"LOCKHARDEX(HNDTOID({handle}),{mode})\n{body}";
        int hookCount = mode == 1 ? 1 : 2;
        Task run = new Interpreter(windows: new Automation(root)).RunAsync(source, host, stop.Token);
        try
        {
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(hookCount, Volatile.Read(ref backend.Active));
            if (failure == "destroyed")
            {
                await fixture.DisposeAsync();
            }
            else
            {
                Volatile.Write(ref platform.Changed, true);
            }
            if (handled)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(["caught", "alive"], host.Output);
            }
            else
            {
                ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                    run.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
                Assert.IsType<InvalidOperationException>(error.InnerException);
            }

            Assert.True(host.Cleaned);
            Assert.Equal(0, backend.Active);
            Assert.Equal(hookCount, backend.Removals);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                failure,
                handled,
                mode,
                registered_hooks = hookCount,
                public_script = source,
                real_target_destroyed = failure == "destroyed",
                identity_injected = failure != "destroyed",
                delay_cleaned = host.Cleaned,
                active = backend.Active,
                removals = backend.Removals,
                lines = host.Output,
                limits = "実フックを使用するが、入力配送や実際のHWND再利用を確認する検証ではない。"
            }));
        }
        finally
        {
            try
            {
                stop.Cancel();
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // 本体の障害を保持し、取消待ちの期限後も実登録の回収へ進みます。
                }
            }
            finally
            {
                // 検証側の監視期限であり、製品の停止予算は延長しません。
                await root.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, backend.Active);
            }
        }
    }

    public static IEnumerable<object[]> FailureCases
    {
        get
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                foreach (string failure in new[] { "destroyed", "pid", "tid" })
                {
                    foreach (bool handled in new[] { false, true })
                    {
                        yield return [failure, handled, mode];
                    }
                }
            }
        }
    }

    private sealed class Platform(string failure) : IWindowIdentityPlatform
    {
        private readonly NativeWindowIdentityPlatform native = new();
        internal bool Changed;

        public WindowIdentity? ReadIdentity(nint window)
        {
            WindowIdentity? identity = native.ReadIdentity(window);
            if (!Volatile.Read(ref Changed) || identity is not { } value)
            {
                return identity;
            }

            // PID/TIDの不一致を注入し、実際のHWND再利用とは区別します。
            return failure == "pid"
                ? value with
                {
                    ProcessId = unchecked(value.ProcessId + 1)
                }
                : value with
                {
                    ThreadId = unchecked(value.ThreadId + 1)
                };
        }
    }

    private sealed class Backend : ISelectiveInputHookBackend
    {
        private readonly NativeSelectiveInputHookBackend native = new();
        internal int Active;
        internal int Removals;

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure procedure)
        {
            (nint handle, int error) = native.Install(type, procedure);
            if (handle != 0)
            {
                Interlocked.Increment(ref Active);
            }

            return (handle, error);
        }

        public (bool Success, int Error) Remove(nint hook)
        {
            (bool success, int error) = native.Remove(hook);
            if (success)
            {
                Interlocked.Decrement(ref Active);
            }

            Interlocked.Increment(ref Removals);
            return (success, error);
        }
    }

    private sealed class Automation(IWindowsAutomationSession root) : IWindowsAutomation
    {
        public IWindowsAutomationSession CreateSession() => root;
    }

    private sealed class Host : IScriptHost
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Output = [];
        internal bool Cleaned;

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public async Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                Cleaned = true;
            }
        }
    }
}
