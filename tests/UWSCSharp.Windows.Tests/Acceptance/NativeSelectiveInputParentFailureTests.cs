using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeSelectiveInputParentFailureTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task PublicParentBoundsFailureForwardsSuspendsAndReleases(
        int mode, bool handled)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        await using PumpedControlFixture target = await PumpedControlFixture.CreateAsync(_ => { });
        nint child = 0;
        await using PumpedControlFixture parent = await PumpedControlFixture.CreateAsync(
            window => child = window.Child("STATIC", "矩形照会の子", 0, 10));
        var backend = new Backend();
        Session? hook = null;
        Context? context = null;
        IWindowsAutomationSession root = new WindowsAutomation(
            _ => throw new InvalidOperationException("入力送信は使用しません。"),
            swappedMouseButtons: false, selectiveInputFactory: (selection, devices) =>
            {
                context = new Context(parent, child);
                hook = new Session(new NativeSelectiveInputHookSession(
                    selection, devices, backend, context), selection, context, backend);
                return hook;
            }).CreateSession();
        var host = new Host();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string handle = ((long)target.Root).ToString(CultureInfo.InvariantCulture);
        string body = handled
            ? "TRY\n SLEEP(99)\nEXCEPT\n PRINT \"caught\"\nENDTRY\nPRINT \"alive\""
            : "SLEEP(99)";
        Task? run = null;
        try
        {
            run = new Interpreter(windows: new Automation(root)).RunAsync(
                $"LOCKHARDEX(HNDTOID({handle}),{mode})\n{body}", host, caller.Token);
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref backend.Active));
            Assert.NotNull(hook);
            Assert.NotNull(context);
            Volatile.Write(ref hook.Armed, 1);
            if (handled)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(["caught", "alive"], host.Lines);
            }
            else
            {
                ScriptException error = await Assert.ThrowsAsync<ScriptException>(
                    () => run.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Same(context.Error, error.InnerException);
                Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
            }
            Assert.False(caller.IsCancellationRequested);
            Assert.True(host.Cleaned);
            Assert.True(context.ParentDestroyed);
            Assert.NotEqual(0, context.DwmResult);
            Assert.False(context.FallbackSucceeded);
            Assert.NotNull(context.Error);
            Assert.NotEqual(0, context.Error.NativeErrorCode);
            Assert.Equal(1, context.Resolutions);
            Assert.Equal(1, context.BoundsReads);
            Assert.Equal(0, context.ForegroundReads);
            Assert.Equal(4, context.Forwards);
            Assert.Equal([17L, 17L, 17L, 17L], hook.Results);
            Assert.Equal(2, backend.Removals);
            Assert.Equal(0, backend.Active);
            output.WriteLine(JsonSerializer.Serialize(new
            {
                mode,
                handled,
                parent_destroyed = context.ParentDestroyed,
                dwm_result = context.DwmResult,
                fallback_succeeded = context.FallbackSucceeded,
                error_code = context.Error.NativeErrorCode,
                bounds_reads = context.BoundsReads,
                resolutions = context.Resolutions,
                foreground_reads = context.ForegroundReads,
                forwards = context.Forwards,
                callback_results = hook.Results,
                removals = backend.Removals,
                active = backend.Active,
                delay_cleaned = host.Cleaned,
                limits = "通知は所有スレッド上の直接呼出し。実親を照会後に制御破棄し、実Win32の両矩形API失敗を確認する。物理配送や自然発生の競合ではない。"
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

    private sealed class Backend : ISelectiveInputHookBackend
    {
        private readonly NativeSelectiveInputHookBackend native = new();
        internal SelectiveInputHookProcedure? Mouse;
        internal SelectiveInputHookProcedure? Keyboard;
        internal int Active;
        internal int Removals;

        public (nint Handle, int Error) Install(int type, SelectiveInputHookProcedure callback)
        {
            if (type == 13)
            {
                Keyboard = callback;
            }
            if (type == 14)
            {
                Mouse = callback;
            }
            (nint handle, int error) = native.Install(type, callback);
            if (handle != 0)
            {
                Interlocked.Increment(ref Active);
            }
            return (handle, error);
        }

        public (bool Success, int Error) Remove(nint handle)
        {
            (bool success, int error) = native.Remove(handle);
            if (success)
            {
                Interlocked.Decrement(ref Active);
                Interlocked.Increment(ref Removals);
            }
            return (success, error);
        }
    }

    private sealed class Session(ISelectiveInputHookSession inner,
        SelectiveInputTarget target, Context context, Backend backend) : ISelectiveInputHookSession
    {
        internal int Armed;
        internal readonly List<long> Results = [];
        private bool injected;

        public bool IsRegistered => inner.IsRegistered;

        public void Start() => inner.Start();

        public bool Pump()
        {
            if (!injected && Volatile.Read(ref Armed) != 0)
            {
                injected = true;
                nint data = Marshal.AllocHGlobal(32);
                context.Injecting = true;
                try
                {
                    Marshal.Copy(new byte[32], 0, data, 32);
                    // 追加情報1の通知は親を照会せず、そのまま転送します。
                    Marshal.WriteIntPtr(data, 24, 1);
                    Results.Add((long)backend.Mouse!(0, 0x207, data));
                    Assert.Equal(0, context.Resolutions);
                    Marshal.WriteIntPtr(data, 24, 0);
                    Results.Add((long)backend.Mouse(0, 0x207, data));
                    // 最初の照会失敗後は、他の通知でも対象を再照会しません。
                    Results.Add((long)backend.Mouse(0, 0x207, data));
                    Marshal.Copy(new byte[32], 0, data, 32);
                    Marshal.WriteInt32(data, 0x87);
                    Results.Add((long)backend.Keyboard!(0, 0x100, data));
                }
                finally
                {
                    context.Injecting = false;
                    Marshal.FreeHGlobal(data);
                }
            }
            return inner.Pump();
        }

        public void Wait(AutoResetEvent work, int milliseconds) => inner.Wait(work, milliseconds);

        public void Stop() => inner.Stop();

        public void Suspend() => inner.Suspend();

        public void PrepareStop(TimeProvider clock, long start) => inner.PrepareStop(clock, start);

        public void ConfigureEmergency(SelectiveInputEmergencyStop emergency) => inner.ConfigureEmergency(emergency);

        public int TakeStopAttempts() => inner.TakeStopAttempts();

        public void RefreshTargets()
        {
            target.Validate();
            context.Refresh();
        }
    }

    private sealed class Context : ISelectiveInputHookContext, ISelectiveInputWindowTree
    {
        private readonly PumpedControlFixture parent;
        private readonly nint child;
        private NativeSelectiveInputWindowTree tree;
        private readonly NativeSelectiveInputHookContext native;
        internal bool Injecting;
        internal bool ParentDestroyed;
        internal int DwmResult;
        internal bool FallbackSucceeded;
        internal int Resolutions;
        internal int BoundsReads;
        internal int ForegroundReads;
        internal int Forwards;
        internal Win32Exception? Error;

        internal Context(PumpedControlFixture parent, nint child)
        {
            this.parent = parent;
            this.child = child;
            tree = NativeSelectiveInputWindowTree.Capture();
            native = new NativeSelectiveInputHookContext(tree);
        }

        public nint Foreground()
        {
            if (Injecting)
            {
                ForegroundReads++;
            }
            return native.Foreground();
        }

        public nint ResolvePoint(int x, int y)
        {
            if (!Injecting)
            {
                return native.ResolvePoint(x, y);
            }
            Resolutions++;
            return SelectiveInputTargetResolver.Resolve(child, this);
        }

        public nint Next(int code, nuint message, nint data)
        {
            if (!Injecting)
            {
                return native.Next(code, message, data);
            }
            Forwards++;
            return 17;
        }

        public IReadOnlyList<int> PressedEmergencyKeys() => native.PressedEmergencyKeys();

        internal void Refresh()
        {
            tree = NativeSelectiveInputWindowTree.Capture();
            native.RefreshTargets();
        }

        public bool IsVisibleTopLevel(nint window) => tree.IsVisibleTopLevel(window);

        public nint GetParent(nint window)
        {
            nint found = tree.GetParent(window);
            Assert.Equal(parent.Root, found);
            // 親取得と矩形照会の間で、別の所有スレッドの実ウィンドウを破棄します。
            parent.DisposeAsync().AsTask().GetAwaiter().GetResult();
            ParentDestroyed = !Native.IsWindow(found);
            Assert.True(ParentDestroyed);
            return found;
        }

        public bool HasArea(nint window)
        {
            BoundsReads++;
            DwmResult = Native.DwmGetWindowAttribute(window, 9,
                out Native.Rect _, Marshal.SizeOf<Native.Rect>());
            FallbackSucceeded = Native.GetWindowRect(window, out _);
            try
            {
                return tree.HasArea(window);
            }
            catch (Win32Exception error)
            {
                Error = error;
                throw;
            }
        }
    }

    private sealed class Automation(IWindowsAutomationSession root) : IWindowsAutomation
    {
        public IWindowsAutomationSession CreateSession() => root;
    }

    private sealed class Host : IScriptHost
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Lines = [];
        internal bool Cleaned;

        public ValueTask WriteOutputAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Lines.Add(text);
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
