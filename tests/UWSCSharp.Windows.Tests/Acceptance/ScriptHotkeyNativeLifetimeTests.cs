using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class ScriptHotkeyNativeLifetimeTests
{
    [Theory]
    [InlineData("normal")]
    [InlineData("exit")]
    [InlineData("cancel")]
    [InlineData("error")]
    public async Task CoreTerminationReleasesNativeModifierHookAndAllowsNextRun(string ending)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var automation = new Automation();
        var interpreter = new Interpreter(windows: automation);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool ready = false;
        var host = new CallbackScriptHost(_ =>
        {
            ready = true;
            if (ending == "cancel")
            {
                cancellation.Cancel();
            }
        });
        string tail = ending switch
        {
            "exit" => "EXITEXIT(7)",
            "cancel" => "SLEEP(60)",
            "error" => "PRINT missing_native_lifetime_variable",
            _ => ""
        };
        try
        {
            Task<int> run = interpreter.RunWithExitCodeAsync(Script(tail), host,
                cancellation.Token, ScriptRunOptions.Empty);
            if (ending == "cancel")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            }
            else if (ending == "error")
            {
                Exception error = await Assert.ThrowsAnyAsync<Exception>(() => run);
                Assert.Contains("missing_native_lifetime_variable", error.ToString());
            }
            else
            {
                Assert.Equal(ending == "exit" ? 7 : 0, await run);
            }

            Assert.True(ready);
            AssertReleased(Assert.Single(automation.Backends));
            Assert.Equal(0, await interpreter.RunWithExitCodeAsync(Script(""),
                new CallbackScriptHost(_ => { }), default, ScriptRunOptions.Empty));
            Assert.Equal(2, automation.Backends.Count);
            AssertReleased(automation.Backends[1]);
        }
        finally
        {
            // 検査に失敗した場合も、このテストが作った通知元だけを回収します。
            foreach (WindowsScriptHotkeySource source in automation.Sources)
            {
                await source.DisposeAsync();
            }
        }
    }

    private static string Script(string tail) => $$"""
        IF !SETHOTKEY(0,6,"Pressed") THEN EXITEXIT(99)
        PRINT "registered"
        {{tail}}
        PROCEDURE Pressed()
        FEND
        """;

    private static void AssertReleased(Backend backend)
    {
        Assert.Equal(1, backend.Installed);
        Assert.Equal(1, backend.Removed);
        Assert.Equal(backend.InstallThread, backend.RemoveThread);
        Assert.NotEqual(0u, backend.RemoveThread);
    }

    private sealed class Automation : IWindowsAutomation
    {
        internal readonly List<Backend> Backends = [];
        internal readonly List<WindowsScriptHotkeySource> Sources = [];

        public IWindowsAutomationSession CreateSession() => new Session(this);
    }

    private sealed class Session(Automation owner) : IWindowsAutomationSession
    {
        public IWindowsAutomationSession Fork() => new Session(owner);

        public IScriptHotkeySource CreateHotkeySource()
        {
            var backend = new Backend();
            var source = new WindowsScriptHotkeySource(new NativeScriptHotkeyPlatform(backend));
            owner.Backends.Add(backend);
            owner.Sources.Add(source);
            return source;
        }

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken token) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Backend : IModifierHookBackend
    {
        private readonly NativeModifierHookBackend native = new();
        internal int Installed;
        internal int Removed;
        internal uint InstallThread;
        internal uint RemoveThread;

        public nint Install(ModifierHookProcedure procedure)
        {
            nint handle = native.Install(procedure);
            if (handle != 0)
            {
                Installed++;
                InstallThread = Native.GetCurrentThreadId();
            }

            return handle;
        }

        public bool Remove(nint hook)
        {
            bool removed = native.Remove(hook);
            if (removed)
            {
                Removed++;
                RemoveThread = Native.GetCurrentThreadId();
            }

            return removed;
        }

        public bool IsPressed(int key) => native.IsPressed(key);

        public nint Next(int code, nuint message, nint data) => native.Next(code, message, data);
    }
}
