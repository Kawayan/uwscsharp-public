using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// Sandbox内の注入入力で、待機中のマウス採取を確認します。
/// </summary>
[Collection("UIA desktop")]
[Trait("Category", "SandboxOnly")]
public sealed class NativeMouseKeyHistoryAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SleepPollKeepsReleasedMouseClickAfterNativeLowBitIsDrained()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        Assert.True(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(0, Native.GetSystemMetrics(23));
        var backend = new ObservingBackend();
        Assert.Equal(0, backend.ReadWithoutObservation() & 0x8000);
        Native.Point savedCursor = ReadCursor();
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(_ => { },
                title: "GETKEYSTATE pending acceptance");
        var host = new GatedHost();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(20));
        Task? running = null;
        Exception? primary = null;
        try
        {
            PositionOverOwnedWindow(fixture.Root);
            var automation = new WindowsAutomation(
                _ => throw new InvalidOperationException(
                    "スクリプトからの入力送信は禁止です。"),
                swappedMouseButtons: false, keyStateBackend: backend);
            running = new Interpreter(windows: automation).RunAsync("""
                SLEEP(60)
                PRINT GETKEYSTATE(VK_LBUTTON)
                PRINT GETKEYSTATE(VK_LBUTTON)
                """, host, cancellation.Token);
            await host.Entered.WaitAsync(TimeSpan.FromSeconds(10),
                cancellation.Token);

            SendMouse(up: false);
            short down = await backend.DownObserved.WaitAsync(
                TimeSpan.FromSeconds(10), cancellation.Token);
            Assert.NotEqual(0, down & 0x8000);
            output.WriteLine($"PollがDOWNを観測: {unchecked((ushort)down):X4}");
            SendMouse(up: true);
            short up = await backend.UpObserved.WaitAsync(
                TimeSpan.FromSeconds(10), cancellation.Token);
            Assert.Equal(0, up & 0x8000);
            output.WriteLine($"PollがUPを観測: {unchecked((ushort)up):X4}");
            short drained = await DrainNativeLowBitAsync(backend,
                cancellation.Token);
            Assert.Equal(0, drained);
            output.WriteLine($"OSの低bit消費後: {drained:X4}");

            // 次の採取でUPを観測したため、先のDOWN採取は公開済みです。
            host.Release();
            await running.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(["1", "0"], host.Output);
            output.WriteLine($"Native mouse: down={unchecked((ushort)down):X4}, "
                + $"up={unchecked((ushort)up):X4}, drained={drained:X4}; "
                + "GETKEYSTATE=1,0");
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            var failures = new List<Exception>();
            try
            {
                SendMouse(up: true);
                Assert.Equal(0, await DrainNativeLowBitAsync(backend,
                    CancellationToken.None));
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

            try
            {
                cancellation.Cancel();
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

            host.Release();
            if (running is not null)
            {
                try
                {
                    await running.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException)
                    when (cancellation.IsCancellationRequested)
                {
                    // 後処理で要求した取消の完了を確認します。
                }
                catch (Exception failure)
                {
                    if (!ReferenceEquals(primary, failure))
                    {
                        failures.Add(failure);
                    }
                }
            }

            try
            {
                RestoreCursor(savedCursor);
                output.WriteLine($"ゲストのカーソルを復元: {savedCursor.X},{savedCursor.Y}");
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

            ReportCleanupFailures(primary, failures);
        }
    }

    private static Native.Point ReadCursor()
    {
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetCursorPos(out Native.Point point));
        return point;
    }

    private static void PositionOverOwnedWindow(nint window)
    {
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.SetWindowPos(window, -1, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate));
        Assert.True(Native.GetWindowRect(window, out Native.Rect bounds));
        var point = new Native.Point(
            bounds.Left + (bounds.Right - bounds.Left) / 2,
            bounds.Top + (bounds.Bottom - bounds.Top) / 2);
        Assert.True(Native.SetCursorPos(point.X, point.Y));
        nint hit = Native.WindowFromPoint(point);
        Assert.True(hit == window || Native.IsChild(window, hit),
            $"所有画面のhit-test: window={window}, hit={hit}");
    }

    private static void RestoreCursor(Native.Point saved)
    {
        using var dpi = new PhysicalDpiScope();
        if (!Native.SetCursorPos(saved.X, saved.Y))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Assert.True(Native.GetCursorPos(out Native.Point actual));
        Assert.Equal(saved.X, actual.X);
        Assert.Equal(saved.Y, actual.Y);
    }

    private static void SendMouse(bool up)
    {
        var input = new Native.Input
        {
            Type = 0,
            Data = new Native.InputUnion
            {
                Mouse = new Native.MouseInput
                {
                    Flags = up ? 0x0004u : 0x0002u,
                    Extra = 1
                }
            }
        };
        NativeInputSender.Send(input);
    }

    private static async Task<short> DrainNativeLowBitAsync(
        ObservingBackend backend, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            token.ThrowIfCancellationRequested();
            short state = backend.ReadWithoutObservation();
            if (state == 0)
            {
                return state;
            }

            await Task.Delay(5, token);
        }

        throw new TimeoutException("UP後のマウス状態が0になりませんでした。");
    }

    private static void ReportCleanupFailures(Exception? primary,
        IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        if (primary is not null)
        {
            IEnumerable<Exception> previous =
                primary.Data["UWSCSharp.CleanupErrors"]
                    is AggregateException aggregate
                        ? aggregate.InnerExceptions : [];
            primary.Data["UWSCSharp.CleanupErrors"] = new AggregateException(
                "Nativeマウス検証の後処理に失敗しました。",
                previous.Concat(failures));
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException("Nativeマウス検証の後処理に失敗しました。",
            failures);
    }

    private sealed class GatedHost : IScriptHost
    {
        private readonly TaskCompletionSource<bool> entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => entered.Task;

        internal List<string> Output
        {
            get;
        } = [];

        internal void Release() => released.TrySetResult(true);

        public ValueTask WriteOutputAsync(string text,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Output.Add(text);
            return ValueTask.CompletedTask;
        }

        public Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            entered.TrySetResult(true);
            return released.Task.WaitAsync(token);
        }
    }

    private sealed class ObservingBackend : IKeyStateBackend
    {
        private readonly NativeKeyStateBackend native = new();
        private readonly TaskCompletionSource<short> downObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<short> upObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int sawDown;

        internal Task<short> DownObserved => downObserved.Task;

        internal Task<short> UpObserved => upObserved.Task;

        internal short ReadWithoutObservation() => native.ReadAsync(1);

        public short ReadAsync(int key)
        {
            short state = native.ReadAsync(key);
            if (key == 1)
            {
                if ((state & 0x8000) != 0)
                {
                    Interlocked.Exchange(ref sawDown, 1);
                    downObserved.TrySetResult(state);
                }
                else if (Volatile.Read(ref sawDown) != 0)
                {
                    upObserved.TrySetResult(state);
                }
            }

            return state;
        }

        public short ReadToggle(int key) => native.ReadToggle(key);

        public bool ReadIme(CancellationToken token) => native.ReadIme(token);
    }
}
