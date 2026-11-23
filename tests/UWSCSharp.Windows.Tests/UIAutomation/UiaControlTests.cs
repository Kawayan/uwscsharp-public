using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// desktopを共有するUIA検証を他のfixtureと並列実行しません。
/// </summary>
[CollectionDefinition("UIA desktop", DisableParallelization = true)]
public sealed class AccessibilityCollection;

[Collection("UIA desktop")]
public sealed class UiaControlTests
{
    private static Task<int> LinkFocusAsync(nint window, bool? focused = null) => Task.Run(() =>
    {
        // LITEMは固定幅整数とUTF16配列だけなので、対象bitnessで配置が変わりません。
        nint pointer = Marshal.AllocHGlobal(4280);
        try
        {
            Marshal.Copy(new byte[4280], 0, pointer, 4280);
            Marshal.WriteInt32(pointer, 3);
            Marshal.WriteInt32(pointer, 12, 1);
            if (focused is not null)
            {
                Marshal.WriteInt32(pointer, 8, focused.Value ? 1 : 0);
                Assert.NotEqual(0, Native.SendMessageW(window, 0x702, 0, pointer));
            }

            Assert.NotEqual(0, Native.SendMessageW(window, 0x703, 0, pointer));
            return Marshal.ReadInt32(pointer, 8) & 1;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Native.Point Point;
        internal uint Private;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [Theory]
    [InlineData(1073741824)]
    [InlineData(32768)]
    public async Task ExplicitAccessibilityInvokesOnlyEnabledVisibleNamedButtonsOnWorker(int route)
    {
        // 所有controlの検証ではホストの前面を変更しません。
        route |= 512;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ready = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint threadId = 0;
        Exception? callbackFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                using var fixture = new ButtonControlTests.Fixture("UIA専用window");
                fixture.Child("BUTTON", "&実行", 0, 10, id: 55);
                fixture.Child("BUTTON", "disabled", 0x08000000, 60);
                nint hidden = fixture.Child("BUTTON", "hidden", 0, 90);
                Native.ShowWindow(hidden, 0);
                fixture.Clicked = (_, command) => clicked.TrySetResult(command & 0xffff);
                threadId = Native.GetCurrentThreadId();
                ready.SetResult(fixture.Root);
                while (GetMessageW(out Message message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }

                callbackFailure = fixture.CallbackFailure;
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
                callbackFailure = exception;
            }
        })
        {
            IsBackground = true
        };
        owner.SetApartmentState(ApartmentState.STA);
        owner.Start();
        try
        {
            nint root = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"))
                .CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)root)], default);
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("実行"), Value.Numeric(route | 1)], default)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(55, await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            foreach (string name in new[] { "disabled", "hidden", "missing" })
            {
                Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                    [id, Value.String(name), Value.Numeric(route)], default));
            }

            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                [id, Value.String("実行"), Value.Numeric(route), Value.Numeric(1), Value.Numeric(2)], default));
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                [Value.Numeric(0), Value.String("実行"), Value.Numeric(route)], default));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", [id, Value.String("実行"), Value.Numeric(route)],
                    cancellation.Token));
            List<string> output = [];
            string routeName = route == 32768 ? "CLK_ACC" : "CLK_UIA";
            await new Interpreter(windows: new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。")))
                .RunAsync($"""
                    id = HNDTOID({(long)root})
                    PRINT CLKITEM(id, "実行", {routeName} OR CLK_BTN OR CLK_BACK)
                    """, output.Add);
            Assert.Equal(["1"], output);
            await session.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await session.InvokeAsync("CLKITEM", [id, Value.String("実行"), Value.Numeric(route)], default));
        }
        finally
        {
            if (threadId != 0)
            {
                Assert.True(PostThreadMessageW(threadId, 0x12, 0, 0));
            }

            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
        }

        Assert.Null(callbackFailure);
    }

    [Theory]
    [InlineData(128, true, false)]
    [InlineData(128, false, false)]
    [InlineData(640, true, false)]
    [InlineData(640, false, false)]
    [InlineData(1073741952, true, false)]
    [InlineData(1073741952, false, false)]
    [InlineData(0, true, false)]
    [InlineData(0, false, false)]
    [InlineData(384, true, true)]
    [InlineData(384, false, true)]
    [InlineData(1073742208, true, true)]
    [InlineData(1073742208, false, true)]
    public async Task LinkOnOffUsesExpectedProviderActions(int route, bool on, bool partial)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string caption = partial ? "実" : "実行";
        var ready = new TaskCompletionSource<(nint Root, nint First, nint Second)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondClicked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int noticeCount = 0;
        ConcurrentQueue<int> noticeCodes = new();
        ConcurrentQueue<string> messages = new();
        uint threadId = 0;
        Exception? callbackFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                using var styles = new VisualStylesContext();
                // 親STATICを透過hit-testにせず、LINKの既定操作が到達できるhostにします。
                using var fixture = new ButtonControlTests.Fixture("UIA link専用window", 0x100);
                nint first = fixture.Child("SysLink", "<a href=\"fixture://first\" id=\"first\">実行</a>", 0, 10, id: 55);
                fixture.Child("SysLink", "<a>disabled</a>", 0x08000000, 60);
                nint hidden = fixture.Child("SysLink", "<a>hidden</a>", 0, 90);
                Native.ShowWindow(hidden, 0);
                nint second = fixture.Child("SysLink", "<a href=\"fixture://second\" id=\"second\">実行</a>", 0, 120, id: 56);
                // 非activateの所有hostだけをtopmostにし、他windowによる遮蔽を防ぎます。
                Assert.True(Native.SetWindowPos(fixture.Root, -1, 0, 0, 0, 0, 0x13));
                var point = new Native.Point(5, 5);
                Assert.True(Native.ClientToScreen(first, ref point));
                Assert.Equal(first, Native.WindowFromPoint(point));
                nint packed = (point.X & 65535) | (point.Y & 65535) << 16;
                Assert.Equal(1, Native.SendMessageW(fixture.Root, 0x84, 0, packed));
                // payload検証後、非同期継続のTaskへ親通知の到達を直接伝えます。
                fixture.Notify = (_, pointer) =>
                {
                    try
                    {
                        NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
                        noticeCodes.Enqueue(notice.Code);
                        if (notice.Code is -2 or -4)
                        {
                            Assert.Equal(0, Marshal.ReadInt32(pointer,
                                Marshal.SizeOf<NativeTabControl.Notification>() + 4));
                            Assert.Equal(notice.Window == first ? 55 : 56, (int)notice.Id);
                            Assert.True(notice.Window == first || notice.Window == second);
                            int sequence = Interlocked.Increment(ref noticeCount);
                            (sequence == 1 ? clicked : secondClicked)
                                .TrySetResult((int)notice.Id);
                        }

                    }
                    catch (Exception exception)
                    {
                        clicked.TrySetException(exception);
                        secondClicked.TrySetException(exception);
                        throw;
                    }

                    return 0;
                };
                threadId = Native.GetCurrentThreadId();
                ready.SetResult((fixture.Root, first, second));
                while (GetMessageW(out Message message, 0, 0, 0) > 0)
                {
                    // 失敗時に初回描画と操作の前後を照合できるよう、所有スレッドの履歴だけを保持します。
                    if (message.Id is 0x0f or 0x100 or 0x101 or 0x113 or 0x201 or 0x202)
                    {
                        messages.Enqueue($"hwnd={message.Window},message={message.Id:X}");
                        while (messages.Count > 64)
                        {
                            messages.TryDequeue(out _);
                        }
                    }

                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }

                callbackFailure = fixture.CallbackFailure;
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
                callbackFailure = exception;
            }
        })
        {
            IsBackground = true
        };
        owner.SetApartmentState(ApartmentState.STA);
        owner.Start();
        try
        {
            (nint root, nint first, nint second) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!on)
            {
                Assert.Equal(0, await LinkFocusAsync(first, false));
                Assert.Equal(0, await LinkFocusAsync(second, false));
            }
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"))
                .CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)root)], default);
            messages.Enqueue("Invoke開始");
            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String(caption), Value.Numeric(route), Value.Numeric(on ? 1 : 0)], default)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15)));
            messages.Enqueue("Invoke復帰");
            int firstId = 0;
            if (on)
            {
                try
                {
                    firstId = await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (TimeoutException error)
                {
                    var firstPoint = new Native.Point(5, 5);
                    var secondPoint = new Native.Point(5, 5);
                    Native.ClientToScreen(first, ref firstPoint);
                    Native.ClientToScreen(second, ref secondPoint);
                    throw new TimeoutException(
                        $"LINK通知待ち: notify={Volatile.Read(ref noticeCount)}, "
                        + $"codes={string.Join(',', noticeCodes)}, firstHit={Native.WindowFromPoint(firstPoint) == first}, "
                        + $"secondHit={Native.WindowFromPoint(secondPoint) == second}, foregroundRoot={Native.GetForegroundWindow() == root}, ownerAlive={owner.IsAlive}, "
                        + $"messages={string.Join(";", messages)}", error);
                }
            }
            if (!on)
            {
                int focused = await LinkFocusAsync(first) + await LinkFocusAsync(second);
                Assert.Equal((route & 512) != 0 ? 0 : 1, focused);
                Assert.Equal(0, Volatile.Read(ref noticeCount));
            }

            Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String(caption), Value.Numeric(route), Value.Numeric(on ? 1 : 0), Value.Numeric(2)], default));
            if (on)
            {
                int secondId = await secondClicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal([55, 56], new[] { firstId, secondId }.Order());
            }
            else
            {
                Assert.Equal(0, Volatile.Read(ref noticeCount));
            }
            foreach (string name in new[] { "disabled", "hidden", "missing" })
            {
                Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                    [id, Value.String(name), Value.Numeric(route), Value.Numeric(on ? 1 : 0)], default));
            }

            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                [id, Value.String(caption), Value.Numeric(route), Value.Numeric(on ? 1 : 0), Value.Numeric(3)], default));
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
                [Value.Numeric(0), Value.String(caption), Value.Numeric(route)], default));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", [id, Value.String(caption), Value.Numeric(route)],
                    cancellation.Token));
            List<string> output = [];
            string routeName = (route & 1073741824) != 0
                ? "CLK_UIA OR CLK_LINK" : "CLK_LINK";
            if (partial)
            {
                routeName += " OR CLK_SHORT";
            }
            await new Interpreter(windows: new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。")))
                .RunAsync($"""
                    id = HNDTOID({(long)root})
                    PRINT CLKITEM(id, "{caption}", {routeName} OR CLK_BACK, {(on ? "TRUE" : "FALSE")})
                    """, output.Add);
            Assert.Equal(["1"], output);
            await session.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await session.InvokeAsync("CLKITEM", [id, Value.String(caption), Value.Numeric(route), Value.Numeric(on ? 1 : 0)], default));
        }
        finally
        {
            if (threadId != 0)
            {
                Assert.True(PostThreadMessageW(threadId, 0x12, 0, 0));
            }

            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
        }

        Assert.Null(callbackFailure);
        if (!on)
        {
            Assert.Equal(0, Volatile.Read(ref noticeCount));
        }
    }
}
