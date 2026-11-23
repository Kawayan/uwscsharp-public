using System.ComponentModel;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputFailureBridgeTests
{
    [Fact]
    public async Task ReportedFaultDoesNotHideNewCleanupFailure()
    {
        var hook = new Hook { StopFailures = 1 };
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        hook.OnStop = () =>
        {
            entered.Set();
            Assert.True(resume.Wait(5000));
        };
        IWindowsAutomationSession root = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力送信を使用しません。"),
            swappedMouseButtons: false, keyStateBackend: new Keys(),
            selectiveInputFactory: (_, _) => hook).CreateSession();
        await Assert.IsAssignableFrom<ISelectiveInputOwnershipSession>(root)
            .SetSelectiveInputAsync(new(0, 1), default);
        try
        {
            Volatile.Write(ref hook.Fail, true);
            Assert.True(entered.Wait(3000));
            WindowsAutomationFailure failure = Assert.IsType<WindowsAutomationFailure>(root.TakeAutomationFailure());
            Assert.Same(hook.Error, failure.Error);
            Task close = root.DisposeAsync().AsTask();
            resume.Set();
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => close);
            AggregateException aggregate = Assert.IsType<AggregateException>(error);
            Assert.Contains(hook.CleanupError, aggregate.Flatten().InnerExceptions);
            Assert.False(hook.IsRegistered);
            Assert.Equal(2, hook.Stops);
        }
        finally
        {
            resume.Set();
            hook.StopFailures = 0;
            await root.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedHookFailureReachesInterpreterAndCanBeHandled(bool handled)
    {
        var hook = new Hook();
        IWindowsAutomationSession root = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力送信を使用しません。"),
            swappedMouseButtons: false, keyStateBackend: new Keys(),
            selectiveInputFactory: (_, _) => hook).CreateSession();
        await Assert.IsAssignableFrom<ISelectiveInputOwnershipSession>(root)
            .SetSelectiveInputAsync(new(0, 1), default);
        var host = new Host();
        using var stop = new CancellationTokenSource();
        string source = handled
            ? "TRY\n SLEEP(99)\nEXCEPT\n PRINT \"caught\"\nENDTRY\nPRINT \"alive\""
            : "SLEEP(99)";
        Task run = new Interpreter(windows: new Automation(root)).RunAsync(source, host, stop.Token);
        try
        {
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Volatile.Write(ref hook.Fail, true);
            if (handled)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(["caught", "alive"], host.Output);
            }
            else
            {
                ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                    run.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Same(hook.Error, error.InnerException);
                Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
                Assert.Contains("5", error.Message);
            }

            Assert.False(hook.IsRegistered);
            Assert.True(hook.Suspended);
            Assert.True(host.Cleaned);
            Assert.Equal(1, hook.Stops);
        }
        finally
        {
            stop.Cancel();
            try
            {
                await run;
            }
            catch
            {
                // 本体で障害を検査し、偽登録の回収を後始末でも必須にします。
            }

            await root.DisposeAsync();
            Assert.False(hook.IsRegistered);
        }
    }

    // 実入力・ウィンドウ・OSフックの代わりに障害と登録状態だけを注入します。
    private sealed class Hook : ISelectiveInputHookSession
    {
        internal readonly Win32Exception Error = new(5, "フック通知の失敗");
        internal readonly Win32Exception CleanupError = new(6, "フック解除の失敗");
        internal Action? OnStop;
        internal int StopFailures;
        internal bool Fail;
        internal bool Suspended;
        internal int Stops;
        private int registered;

        public bool IsRegistered => Volatile.Read(ref registered) != 0;

        public void Start() => Volatile.Write(ref registered, 1);

        public bool Pump()
        {
            if (Volatile.Read(ref Fail))
            {
                throw Error;
            }

            return false;
        }

        public void Wait(AutoResetEvent work, int milliseconds) => work.WaitOne(milliseconds);

        public void Suspend() => Suspended = true;

        public void Stop()
        {
            Suspended = true;
            Interlocked.Increment(ref Stops);
            OnStop?.Invoke();
            if (Interlocked.Decrement(ref StopFailures) >= 0)
            {
                throw CleanupError;
            }

            Volatile.Write(ref registered, 0);
        }

        public void RefreshTargets()
        {
        }
    }

    private sealed class Keys : IKeyStateBackend
    {
        public short ReadAsync(int key) => 0;

        public short ReadToggle(int key) => 0;

        public bool ReadIme(CancellationToken token) => false;
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
