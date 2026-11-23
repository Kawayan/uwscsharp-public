using System.ComponentModel;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class GetKeyStateTests
{
    [Fact]
    public async Task CanceledImeKeepsWorkerUntilNativeQueryCompletes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var left = new ManualResetEventSlim();
        var backend = new NativeKeyStateBackend(() => 10, _ => 20, _ =>
        {
            entered.Set();
            try
            {
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return new(true, 1, 0);
            }
            finally
            {
                left.Set();
            }
        });
        await using IWindowsAutomationSession session = Session(backend);
        using var cancel = new CancellationTokenSource();
        Task<Value> running = session.InvokeAsync("GETKEYSTATE",
            [Value.Numeric(10004)], cancel.Token).AsTask();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            cancel.Cancel();
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancel.Token, failure.CancellationToken);
            Assert.True(NativeControlWorker.Active > 0);
        }
        finally
        {
            release.Set();
            Assert.True(left.Wait(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(7, await NativeControlWorker.RunAsync(_ => 7, default));
    }

    [Fact]
    public async Task ImeQueryRunsOffTheCallingThread()
    {
        int caller = Environment.CurrentManagedThreadId;
        int observed = 0;
        var backend = new NativeKeyStateBackend(() => 10, _ => 20, _ =>
        {
            observed = Environment.CurrentManagedThreadId;
            return new(true, 0, 0);
        });
        await using IWindowsAutomationSession session = Session(backend);
        await session.InvokeAsync("GETKEYSTATE", [Value.Numeric(10004)], default);
        Assert.NotEqual(caller, observed);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    public async Task ScriptObservesLogicalTypeForKeyToggleAndIme(
        short state, string expected)
    {
        var backend = new Backend { State = state };
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            swappedMouseButtons: false, keyStateBackend: backend);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            key = GETKEYSTATE(VK_A)
            toggle = GETKEYSTATE(TGL_NUMLOCK)
            ime = GETKEYSTATE(TGL_IME)
            PRINT VARTYPE(key)
            PRINT VARTYPE(toggle)
            PRINT VARTYPE(ime)
            PRINT key + 0
            PRINT toggle + 0
            PRINT ime + 0
            """, output.Add);
        Assert.Equal(["11", "11", "11", expected, expected, expected],
            output);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(-32768, 1)]
    [InlineData(-32767, 1)]
    [InlineData(16, 1)]
    public async Task OrdinaryKeysUseTheWholeAsyncWord(short state, int expected)
    {
        var backend = new Backend { State = state };
        await using IWindowsAutomationSession session = Session(backend);
        Assert.Equal(expected, (await session.InvokeAsync("GETKEYSTATE",
            [Value.Numeric(65)], default)).Number);
        Assert.Equal(["async:65"], backend.Calls);
    }

    [Theory]
    [InlineData(10000, 144)]
    [InlineData(10001, 20)]
    [InlineData(10002, 145)]
    [InlineData(10003, 21)]
    public async Task ToggleUsesOnlyLowBit(int code, int key)
    {
        var backend = new Backend { State = -32768 };
        await using IWindowsAutomationSession session = Session(backend);
        Assert.Equal(0, (await session.InvokeAsync("GETKEYSTATE",
            [Value.Numeric(code)], default)).Number);
        backend.State = 1;
        Assert.Equal(1, (await session.InvokeAsync("GETKEYSTATE",
            [Value.Numeric(code)], default)).Number);
        Assert.Equal([$"toggle:{key}", $"toggle:{key}"], backend.Calls);
    }

    [Theory]
    [InlineData(1, true, 2)]
    [InlineData(2, true, 1)]
    [InlineData(4, true, 4)]
    [InlineData(1, false, 1)]
    [InlineData(901, true, 13)]
    public async Task MouseSwapAndRightEnter(int code, bool swapped, int expected)
    {
        var backend = new Backend();
        await using IWindowsAutomationSession session = Session(backend, swapped);
        await session.InvokeAsync("GETKEYSTATE", [Value.Numeric(code)], default);
        Assert.Equal([$"async:{expected}"], backend.Calls);
    }

    [Fact]
    public async Task ImeAndCancellationAndDisposal()
    {
        var backend = new Backend { State = 1 };
        IWindowsAutomationSession session = Session(backend);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETKEYSTATE", [Value.Numeric(10004)], cancel.Token));
        Assert.Empty(backend.Calls);
        Assert.Equal(1, (await session.InvokeAsync("GETKEYSTATE",
            [Value.Numeric(10004)], default)).Number);
        Assert.Equal(["ime"], backend.Calls);
        using var during = new CancellationTokenSource();
        backend.AfterRead = during.Cancel;
        OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETKEYSTATE", [Value.Numeric(65)], during.Token));
        Assert.Equal(during.Token, failure.CancellationToken);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync("GETKEYSTATE", [Value.Numeric(65)], default));
    }

    [Fact]
    public void NonFiniteAndOutOfRangeFailBeforeRead()
    {
        foreach (double code in new[] { double.NaN, double.PositiveInfinity, -1, 256, 10005 })
        {
            Assert.Throws<InvalidOperationException>(() =>
                KeyStateRequest.Read(Value.Numeric(code)));
        }
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(10, 0, false)]
    [InlineData(10, 20, true)]
    public void NativeImeSkipsMissingWindows(long window, long ime, bool expected)
    {
        int queries = 0;
        var backend = new NativeKeyStateBackend(() => (nint)window,
            _ => (nint)ime, _ =>
            {
                queries++;
                return new(true, 1, 0);
            });
        Assert.Equal(expected, backend.ReadIme(default));
        Assert.Equal(expected ? 1 : 0, queries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1460)]
    [InlineData(5)]
    public async Task NativeImeFailuresReachExceptAndSessionRecovers(int error)
    {
        bool fail = true;
        var backend = new NativeKeyStateBackend(() => 10, _ => 20,
            _ => fail ? new(false, 0, error) : new(true, 0, 0));
        await using IWindowsAutomationSession session = Session(backend);
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await session.InvokeAsync("GETKEYSTATE", [Value.Numeric(10004)], default));
        Type expected = error switch
        {
            0 => typeof(InvalidOperationException),
            1460 => typeof(TimeoutException),
            _ => typeof(Win32Exception)
        };
        Assert.IsType(expected, exception);
        Assert.NotNull(session.GetScriptErrorMessage("GETKEYSTATE", exception));
        fail = false;
        Assert.Equal(0, (await session.InvokeAsync("GETKEYSTATE",
            [Value.Numeric(10004)], default)).Number);
    }

    [Fact]
    public async Task NativeToggleReadsAndPreservesPrivateThreadState()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            byte[] saved = new byte[256];
            try
            {
                Assert.True(GetKeyboardState(saved));
                try
                {
                    var backend = new NativeKeyStateBackend();
                    foreach (int code in new[] { 10000, 10001, 10002, 10003 })
                    {
                        var request = KeyStateRequest.Read(Value.Numeric(code));
                        byte[] state = (byte[])saved.Clone();
                        state[request.Key] = 0x81;
                        Assert.True(SetKeyboardState(state));
                        Assert.True(KeyStateReader.Read(request, backend, false, default));
                        byte[] after = new byte[256];
                        Assert.True(GetKeyboardState(after));
                        Assert.Equal(0x81, after[request.Key]);
                        state[request.Key] = 0x80;
                        Assert.True(SetKeyboardState(state));
                        Assert.False(KeyStateReader.Read(request, backend, false, default));
                    }
                }
                finally
                {
                    Assert.True(SetKeyboardState(saved));
                }

                completed.SetResult();
            }
            catch (Exception failure)
            {
                completed.SetException(failure);
            }
        });
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(thread.Join(5000));
    }

    private static IWindowsAutomationSession Session(IKeyStateBackend backend,
        bool swapped = false) => new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"),
            swappedMouseButtons: swapped, keyStateBackend: backend).CreateSession();

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState([Out] byte[] state);

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] state);

    private sealed class Backend : IKeyStateBackend
    {
        internal List<string> Calls { get; } = [];

        internal short State
        {
            get; set;
        }

        internal Action? AfterRead
        {
            get; set;
        }

        public short ReadAsync(int key)
        {
            Calls.Add($"async:{key}");
            AfterRead?.Invoke();
            return State;
        }

        public short ReadToggle(int key)
        {
            Calls.Add($"toggle:{key}");
            return State;
        }

        public bool ReadIme(CancellationToken token)
        {
            Calls.Add("ime");
            return State != 0;
        }
    }
}
