using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class LinkDeliveryTests(ITestOutputHelper output)
{
    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint window, uint message, nuint parameter, nint data);

    [Theory]
    [InlineData(false, 128)]
    [InlineData(true, 128)]
    [InlineData(false, 640)]
    [InlineData(true, 640)]
    public async Task OwnedOcclusionChangesAcceptanceAndParentDelivery(bool covered, int route)
    {
        await VerifyDeliveryAsync(covered, route, inputDesktop: true);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(640)]
    public async Task OffDoesNotNotify(int route)
    {
        await VerifyDeliveryAsync(false, route, inputDesktop: true, on: false);
    }

    /// <summary>
    /// 専用プロセスからも同じ通知条件を検証します。
    /// </summary>
    internal async Task VerifyDeliveryAsync(bool covered, int route,
        bool inputDesktop, bool on = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int notices = 0;
        int markers = 0;
        nint link = 0;
        nint cover = 0;
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var trace = new LinkTrace();
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            link = host.Child("SysLink", "<a id=\"target\">実行</a>", 0, 10, id: 55);
            trace.Attach(link);
            host.MenuCommand = _ =>
            {
                Interlocked.Increment(ref markers);
                acknowledged.TrySetResult();
            };
            host.Notify = (_, pointer) =>
            {
                NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
                trace.Messages.Enqueue($"notify:{notice.Code}");
                if (notice.Code is -2 or -4)
                {
                    Interlocked.Increment(ref notices);
                    Assert.True(PostMessageW(host.Root, 0x111, 55 | (1u << 16), 0));
                    notified.TrySetResult();
                }

                return 0;
            };
            Assert.True(Native.SetWindowPos(host.Root, -1, 0, 0, 0, 0, 0x13));
            if (covered)
            {
                Assert.True(Native.GetWindowRect(host.Root, out Native.Rect rectangle));
                // 所有popupだけでLINKを覆い、他appを操作しません。
                cover = Native.CreateWindowExW(0, "STATIC", "owned cover", 0x80000100,
                    rectangle.Left, rectangle.Top, 300, 200, host.Root, 0, 0, 0);
                Assert.NotEqual(0, cover);
                Native.ShowWindow(cover, 4);
                Assert.True(Native.SetWindowPos(cover, -1, 0, 0, 0, 0, 0x13));
            }
        });
        var point = new Native.Point(5, 5);
        Assert.True(Native.ClientToScreen(link, ref point));
        nint before = Native.WindowFromPoint(point);
        Assert.True(NativeLinkAvailability.CanInvoke(fixture.Root));
        Assert.True(NativeLinkAvailability.CanInvoke(link) == inputDesktop,
            $"SysLinkの入力デスクトップ条件が異なります。期待={inputDesktop}");
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value result = await session.InvokeAsync("CLKITEM",
            [id, Value.String("実行"), Value.Numeric(route), Value.Numeric(on ? 1 : 0)], default);
        bool accepted = !covered && (!on || inputDesktop);
        bool delivered = accepted && on;
        Assert.Equal(Value.Numeric(accepted ? 1 : 0), result);
        Assert.Equal(covered ? cover : link, before);
        if (delivered)
        {
            try
            {
                await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"LINK通知: notices={notices}, markers={markers}, "
                    + $"beforeLink={before == link}, afterLink={Native.WindowFromPoint(point) == link}, "
                    + $"foregroundRoot={Native.GetForegroundWindow() == fixture.Root}, "
                    + $"messages={string.Join(';', trace.Messages)}", error);
            }
        }

        output.WriteLine($"covered={covered}, route={route}, on={on}, inputDesktop={inputDesktop}, result={result.Display()}, beforeLink={before == link}, beforeCover={before == cover}, notices={Volatile.Read(ref notices)}, markers={Volatile.Read(ref markers)}, afterLink={Native.WindowFromPoint(point) == link}, foregroundRoot={Native.GetForegroundWindow() == fixture.Root}, messages={string.Join(';', trace.Messages)}");
        fixture.Dispose();
        if (cover != 0)
        {
            Assert.False(Native.IsWindow(cover));
        }
        Assert.Equal(delivered ? 1 : 0, Volatile.Read(ref notices));
        Assert.Equal(delivered ? 1 : 0, Volatile.Read(ref markers));
    }

    private sealed class LinkTrace : IDisposable
    {
        private delegate nint Procedure(nint window, uint message,
            nint parameter, nint value, nuint id, nuint data);

        private readonly Procedure procedure;

        [DllImport("user32.dll")]
        private static extern nint GetCapture();

        internal ConcurrentQueue<string> Messages { get; } = new();

        internal LinkTrace()
        {
            procedure = (window, message, parameter, value, _, _) =>
            {
                // 既定処理後のマウス捕捉状態を記録します。
                nint result = DefSubclassProc(window, message, parameter, value);
                if (Messages.Count < 128
                    && message is >= 0x200 and <= 0x215 or 7 or 8 or 0x2a3 or 0x1f)
                {
                    Messages.Enqueue($"{message:x}:{parameter:x}:{value:x}:capture={GetCapture() == window}");
                }

                return result;
            };
        }

        internal void Attach(nint window) => Assert.True(
            SetWindowSubclass(window, procedure, 1, 0));

        /// <summary>
        /// 先に破棄される子ウィンドウのcallbackを解放まで保持します。
        /// </summary>
        public void Dispose() => GC.KeepAlive(procedure);

        [DllImport("comctl32.dll")]
        private static extern bool SetWindowSubclass(nint window,
            Procedure procedure, nuint id, nuint data);

        [DllImport("comctl32.dll")]
        private static extern nint DefSubclassProc(nint window,
            uint message, nint parameter, nint value);
    }
}
