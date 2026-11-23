using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;
// 所有画面だけを操作します。前面化の補助入力はゲスト内で行い、製品が送る入力と区別します。
[Collection("UIA desktop")]
public sealed class NativeWindowsTests(ITestOutputHelper diagnostic)
{
    [Fact]
    public async Task M3ControlsSupportScriptAndInvalidTargets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint second = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("Button", "選択(&A)", 3, 10, height: 20, width: 100);
            second = host.Child("Button", "選択", 6, 40, height: 20, width: 100);
            host.Child("Edit", "無効", 0x08000000, 70, height: 20, width: 100);
            host.Child("Edit", "日本語", 0, 100, height: 20, width: 100);
            host.Child("Static", "表示", 0, 130, height: 20, width: 100);
        });
        nint root = fixture.Root;
        await PrepareButtonFixtureAsync(root);
        List<Native.Input> physical = [];
        var automation = new WindowsAutomation(physical.Add, listSelectionPlatform: new OwnedButtonMessagePlatform(fixture.Invoke));
        var runner = new Interpreter(windows: automation);
        List<string> output = [];
        // UIA fallback中も所有STAがmessageを処理します。
        await runner.RunAsync($"""
            id = HNDTOID({(long)root})
            PRINT CHKBTN(id, "選択")
            PRINT CLKITEM(id, "選択", CLK_BTN OR CLK_API OR CLK_BACK)
            PRINT CLKITEM(id, "選択")
            PRINT CHKBTN(id, "選択")
            PRINT CHKBTN(id, "選択", 2)
            PRINT CLKITEM(id, "選択", , TRUE, 2)
            PRINT CLKITEM(id, "選択", , FALSE, 2)
            PRINT CHKBTN(id, "選択", 2)
            PRINT GETSTR(id, 1)
            PRINT GETSTR(id, -1, STR_EDIT)
            PRINT GETSTR(id, 1, STR_STATIC)
            PRINT CLKITEM(id, "missing", CLK_API)
            PRINT CHKBTN(id, "missing")
            PRINT GETSTR(id, 100) = EMPTY
            PRINT GETSTR(id, -2147483648) = EMPTY
            """, text =>
            {
                output.Add(text);
                TraceButtonState(root, text);
            });
        Assert.Equal(["0", "1", "1", "1", "0", "1", "1", "2",
            "日本語", "無効", "表示", "0", "-1", "1", "1"], output);
        await using IWindowsAutomationSession session = automation.CreateSession();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)root)], default);
        Native.SendMessageTimeoutW(second, 0xf1, 2, 0, 2, 1000, out _);
        Assert.Equal(N(2), await session.InvokeAsync("CHKBTN",
            [id, Value.String("選択"), N(2)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("選択"), N(1), N(1), N(2)], default));
        Assert.Equal(N(0), await session.InvokeAsync("CHKBTN",
            [id, Value.String("選択"), N(2)], default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CLKITEM",
                [id, Value.String("選択")], cancellation.Token));
        fixture.Dispose();
        Assert.Equal(N(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("選択")], default));
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("選択")], default));
        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR",
            [id, N(1)], default)).Kind);
        Assert.Empty(physical);
    }

    [Fact]
    public async Task M3FocusButtonsAndUnsupportedStylesAreExplicit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            nint edit = host.Child("Edit", "", 0, 10, height: 20, width: 100);
            host.Child("Button", "radio", 9, 40, height: 20, width: 100);
            host.Child("Button", "manual", 2, 70, height: 20, width: 100);
            host.Child("Button", "push", 0, 100, height: 20, width: 100);
            host.Child("Button", "disabled", 0x08000003, 130, height: 20, width: 100);
            nint hidden = host.Child("Button", "hidden", 3, 160, height: 20, width: 100);
            ShowWindow(hidden, 0);
            // BM_CLICKの前提として、所有threadの画面をactiveにしてからfocusを設定します。
            _ = SetActiveWindow(host.Root);
            Assert.Equal(host.Root, GetActiveWindow());
            SetFocus(edit);
        }, title: "M3 boundaries");
        nint root = fixture.Root;
        await PrepareButtonFixtureAsync(root);
        await using IWindowsAutomationSession session =
            new WindowsAutomation(_ => throw new InvalidOperationException(
                "物理入力は禁止です。"), listSelectionPlatform: new OwnedButtonMessagePlatform(fixture.Invoke)).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)root)], default);
        Assert.Equal(Value.String(""), await session.InvokeAsync(
            "GETSTR", [id], default));
        Assert.Equal(Value.String("M3 boundaries"), await session.InvokeAsync(
            "GETSTR", [id, N(0), N(1)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("radio")], default));
        TraceButtonState(root, "radio click");
        Assert.Equal(N(1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("radio")], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("radio"), N(1), N(0)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("manual")], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("push")], default));
        foreach (string label in new[] { "disabled", "hidden" })
        {
            Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
                [id, Value.String(label)], default));
            Assert.Equal(N(-1), await session.InvokeAsync("CHKBTN",
                [id, Value.String(label)], default));
        }

        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync(
            "GETSTR", [N(-1), N(1)], default)).Kind);
        await using IWindowsAutomationSession other =
            new WindowsAutomation().CreateSession();
        Assert.Equal(N(-1), await other.InvokeAsync("CHKBTN",
            [id, Value.String("radio")], default));
    }

    private async Task PrepareButtonFixtureAsync(nint root)
    {
        TraceButtonState(root, "before activation");
        Native.Point point;
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.GetWindowRect(root, out Native.Rect rect));
            // captionのないfixtureでは、子controlを避けた右下を使います。
            point = new Native.Point(rect.Right - 5, rect.Bottom - 5);
        }

        await UiInputDriver.ActivateAsync(root, point);
        TraceButtonState(root, "after activation");
    }

    private void TraceButtonState(nint root, string stage)
    {
        uint thread = Native.GetWindowThreadProcessId(root, out _);
        var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        Assert.True(Native.GetGUIThreadInfo(thread, ref info));
        diagnostic.WriteLine($"{stage}: root={root:X} foreground={Native.GetForegroundWindow():X} active={info.Active:X} focus={info.Focus:X} capture={info.Capture:X}");
    }

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [Theory]
    [InlineData("normal")]
    [InlineData("exit")]
    [InlineData("exitexit")]
    [InlineData("error")]
    [InlineData("cancel")]
    public async Task M1ScriptReleasesDirectInputsOnEveryExit(string ending)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string title = "UWSCSharp-acceptance-" + Guid.NewGuid();
        nint window = CreateWindowExW(0, "STATIC", title,
            0, 30, 40, 240, 160, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            nint edit = CreateWindowExW(0, "EDIT", "start",
                0x50000000, 10, 10, 150, 30, window, 0, 0, 0);
            Assert.NotEqual(0, edit);
            ShowWindow(window, 4);
            var backend = new DirectInputTests.Backend();
            List<Native.Input> physical = [];
            var runner = new Interpreter(windows:
                new WindowsAutomation(physical.Add, backend));
            using var cancellation = new CancellationTokenSource();
            List<string> output = [];
            string script = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "window-basic-operations-acceptance.uws"));
            string tail = ending switch
            {
                "exit" => "EXIT\nPRINT \"unexpected\"",
                "exitexit" => "EXITEXIT\nPRINT \"unexpected\"",
                "error" => "PRINT 1 / 0",
                "cancel" => "PRINT \"unexpected\"",
                _ => ""
            };
            string source = $"fixture_title = \"{title}\"\n"
                + script + "\n" + tail;
            Task Run() => runner.RunAsync(source, text =>
            {
                output.Add(text);
                if (ending == "cancel" && text == "held")
                {
                    cancellation.Cancel();
                }
            }, cancellation.Token);

            if (ending == "error")
            {
                await Assert.ThrowsAsync<ScriptException>(Run);
            }
            else if (ending == "cancel")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            }
            else
            {
                await Run();
            }

            Assert.Equal(["held"], output);
            Assert.Empty(physical);
            Assert.Equal(0x101u, backend.Messages[^2].Message);
            Assert.Equal(0x202u, backend.Messages[^1].Message);
            Assert.Equal(65u, backend.Messages[^2].WParam);
            Assert.Equal(DirectInputSession.Pack(12, 13),
                backend.Messages[^1].LParam);
            var buffer = new StringBuilder(100);
            GetWindowTextW(edit, buffer, 100);
            Assert.Equal("受入確認", buffer.ToString());
            // 同じrunnerで再実行してもID表と入力状態を引き継ぎません。
            await runner.RunAsync($"PRINT GETID(\"{title}\")",
                text => Assert.Equal("1", text));
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [Fact]
    public async Task HandleConversionsPreserveIdsAndRejectExpiredWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint window = CreateWindowExW(0, "STATIC",
            "UWSCSharp-handles-" + Guid.NewGuid(),
            0, 30, 40, 240, 160, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        try
        {
            await using IWindowsAutomationSession first =
                new WindowsAutomation().CreateSession();
            await using IWindowsAutomationSession second =
                new WindowsAutomation().CreateSession();
            var handle = Value.Numeric((long)window);
            Value id = await first.InvokeAsync("HNDTOID", [handle], default);
            Assert.Equal(N(1), id);
            Assert.Equal(id, await first.InvokeAsync("HNDTOID", [handle], default));
            Assert.Equal(handle, await first.InvokeAsync("IDTOHND", [id], default));
            Assert.Equal(N(-1), await first.InvokeAsync("HNDTOID", [N(0)], default));
            Assert.Equal(N(0), await first.InvokeAsync("IDTOHND", [N(-1)], default));
            Assert.Equal(N(0), await first.InvokeAsync("IDTOHND", [N(2)], default));
            // 非ゼロ値の登録時は検証せず、逆引き時にIsWindowで判定します。
            Assert.Equal(N(1), await second.InvokeAsync("HNDTOID", [N(-1)], default));
            Assert.Equal(N(0), await second.InvokeAsync("IDTOHND", [N(1)], default));
            Assert.Equal(N(2), await second.InvokeAsync("HNDTOID", [handle], default));
            Assert.Equal(handle, await first.InvokeAsync("IDTOHND", [id], default));
            if (IntPtr.Size == 8)
            {
                var wide = Value.Numeric(4294967297d);
                Value wideId = await first.InvokeAsync("HNDTOID", [wide], default);
                Assert.Equal(N(2), wideId);
                Assert.Equal(N(0), await first.InvokeAsync("IDTOHND", [wideId], default));
            }

            DestroyWindow(window);
            window = 0;
            Assert.Equal(N(0), await first.InvokeAsync("IDTOHND", [id], default));
            Assert.Equal(id, await first.InvokeAsync("HNDTOID", [handle], default));
        }
        finally
        {
            if (window != 0)
            {
                DestroyWindow(window);
            }
        }
    }

    [Fact]
    public async Task DirectOriginRoutesInputsAndReleasesAfterSwitchingMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string title = "UWSCSharp-direct-" + Guid.NewGuid();
        nint window = CreateWindowExW(0, "STATIC", title,
            0, 30, 40, 240, 160, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        var backend = new DirectInputTests.Backend();
        var physical = new List<Native.Input>();
        IWindowsAutomationSession session = new WindowsAutomation(
            physical.Add, backend).CreateSession();
        try
        {
            Value id = await session.InvokeAsync("GETID",
                [Value.String(title)], default);
            await session.InvokeAsync("MOUSEORG", [id, N(2)], default);
            await session.InvokeAsync("MMV", [N(12), N(13)], default);
            Assert.Equal(window, backend.Messages[0].Window);
            Assert.Equal(0x200u, backend.Messages[0].Message);
            Assert.Equal(0x000d000cu, backend.Messages[0].LParam);
            await session.InvokeAsync("BTN",
                [N(0), N(1), N(12), N(13)], default);
            await session.InvokeAsync("KBD", [N(65), N(1)], default);
            Assert.Equal(12, backend.SelectedClick!.Value.X);
            Assert.Empty(physical);
            await session.InvokeAsync("MOUSEORG", [N(0)], default);
            await session.InvokeAsync("KBD", [N(66)], default);
            Assert.Equal(2, physical.Count);
            int directCount = backend.Messages.Count;
            await session.DisposeAsync();
            Assert.Equal(directCount + 2, backend.Messages.Count);
            Assert.Equal(0x101u, backend.Messages[^2].Message);
            Assert.Equal(0x202u, backend.Messages[^1].Message);
            Assert.All(backend.Messages.Skip(1),
                message => Assert.Equal((nint)20, message.Window));
        }
        finally
        {
            await session.DisposeAsync();
            DestroyWindow(window);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-4)]
    public void NativeDirectSelectorUsesOwnedEditAndClientCoordinates(
        int awareness)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint previous = Native.SetThreadDpiAwarenessContext(awareness);
        Assert.NotEqual(0, previous);
        nint window = 0;
        try
        {
            nint edit;
            // 作成座標を、入力先選択へ渡す物理ピクセルと揃えます。
            using (var dpi = new PhysicalDpiScope())
            {
                window = CreateWindowExW(0, "STATIC",
                    "UWSCSharp-selector-" + Guid.NewGuid(),
                    0, 30, 40, 240, 160, 0, 0, 0, 0);
                Assert.NotEqual(0, window);
                edit = CreateWindowExW(0, "EDIT", "",
                    0x50000000, 10, 20, 100, 30, window, 0, 0, 0);
                Assert.NotEqual(0, edit);
                var origin = new Native.Point(0, 0);
                Assert.True(Native.ClientToScreen(window, ref origin));
                Assert.True(Native.GetWindowRect(edit, out Native.Rect rect));
                Assert.Equal(10, rect.Left - origin.X);
                Assert.Equal(20, rect.Top - origin.Y);
                Assert.Equal(100, rect.Right - rect.Left);
                Assert.Equal(30, rect.Bottom - rect.Top);
                diagnostic.WriteLine(
                    $"context={awareness} origin={origin.X},{origin.Y} " +
                    $"edit={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
            }

            nint caller = GetThreadDpiAwarenessContext();
            var backend = new NativeDirectInputBackend();
            Assert.Equal(edit, backend.SelectKeyboard(window, null));
            Assert.Equal(caller, GetThreadDpiAwarenessContext());
            DirectTarget selected = backend.SelectMouse(window, new(15, 25));
            Assert.Equal(edit, selected.Window);
            Assert.Equal(5, selected.Position.X);
            Assert.Equal(5, selected.Position.Y);
            Assert.Equal(caller, GetThreadDpiAwarenessContext());
            Assert.Equal(edit, backend.SelectKeyboard(window, new(15, 25)));
            Assert.Equal(caller, GetThreadDpiAwarenessContext());
            Assert.Equal(window,
                backend.SelectMouse(window, new(200, 120)).Window);
            Assert.Equal(caller, GetThreadDpiAwarenessContext());
        }
        finally
        {
            if (window != 0)
            {
                DestroyWindow(window);
            }

            Assert.NotEqual(0, Native.SetThreadDpiAwarenessContext(previous));
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint ex,
        string cls, string title, uint style, int x, int y,
        int width, int height, nint parent, nint menu,
        nint instance, nint data);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window,
        int command);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window,
        StringBuilder text, int count);

    private static Value N(int n) => Value.Numeric(n);

    [Fact]
    public async Task FindsAndControlsOwnedWindowAndEdit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string title = "UWSCSharp-test-" + Guid.NewGuid();
        nint window = CreateWindowExW(0, "STATIC", title, 0,
            30, 40, 240, 160, 0, 0, 0, 0);
        Assert.NotEqual(0, window);
        nint edit = CreateWindowExW(0, "EDIT", "start",
            0x50000000, 10, 10, 150, 30, window, 0, 0, 0);
        Assert.NotEqual(0, edit);
        // 表示中の子ウィンドウの選択を検証するため、アクティブ化せずに表示します。
        ShowWindow(window, 4);
        try
        {
            await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
            // 即座に成功する呼び出しはウィンドウを所有するスレッド上で完了するため、SendMessageTimeoutによるデッドロックは発生しません。
            Value id = await session.InvokeAsync("GETID",
                [Value.String(title)], default);
            Assert.True(id.Number > 0);
            Assert.Equal(id, await session.InvokeAsync(
                "GETID", [Value.String(title)], default));
            Assert.Equal(Value.String(title),
                await session.InvokeAsync("STATUS", [id, N(9)], default));
            Assert.Equal(N(1), await session.InvokeAsync(
                "STATUS", [id, N(21)], default));
            Assert.Equal(N(-1), await session.InvokeAsync(
                "STATUS", [id, N(16)], default));
            Assert.Equal(N(Environment.ProcessId),
                await session.InvokeAsync("STATUS", [id, N(18)], default));
            Assert.Equal(N(Environment.Is64BitProcess ? 1
                : 0), await session.InvokeAsync("STATUS", [id, N(19)], default));
            Assert.EndsWith("testhost.exe",
                (await session.InvokeAsync("STATUS", [id, N(17)],
                default)).Text, StringComparison.OrdinalIgnoreCase);
            await session.InvokeAsync("ACW", [id, N(50), N(
                60), N(300), N(200)], default);
            Assert.Equal(N(50), await session.InvokeAsync(
                "STATUS", [id, N(1)], default));
            Assert.Equal(N(300), await session.InvokeAsync(
                "STATUS", [id, N(3)], default));
            await session.InvokeAsync("SENDSTR",
                [id, Value.String("日本語"), N(1), N(1)], default);
            await session.InvokeAsync("SENDSTR",
                [id, Value.String("追加"), N(1)], default);
            var buffer = new StringBuilder(100);
            GetWindowTextW(edit, buffer, 100);
            Assert.Equal("日本語追加", buffer.ToString());
            await session.InvokeAsync("CTRLWIN", [id, N(4)],
                default);
            Assert.Equal(N(0), await session.InvokeAsync(
                "STATUS", [id, N(12)], default));
            DestroyWindow(window);
            window = 0;
            Assert.Equal(N(0), await session.InvokeAsync(
                "STATUS", [id, N(21)], default));
            Assert.Equal(Value.String(""),
                await session.InvokeAsync("STATUS", [id, N(9)], default));
        }
        finally
        {
            if (window != 0)
            {
                DestroyWindow(window);
            }
        }
    }

    [Fact]
    public async Task GetIdWaitCanBeCancelledAndSessionRejectsCallsAfterDisposal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        using var cancellation = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await session.InvokeAsync("GETID",
            [Value.String(Guid.NewGuid().ToString()), Value.String(
            ""), N(-1)], cancellation.Token));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await session.InvokeAsync("STATUS", [N(1), N(
            21)], default));
        await session.DisposeAsync();
    }
    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetActiveWindow();

}
