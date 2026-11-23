using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using static UWSCSharp.Wpf.VerificationAssert;

namespace UWSCSharp.Wpf;

internal static class WindowPositionVerification
{
    internal static async Task<int> RunAsync(string receipt)
    {
        if (!Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("画面配置の検証はWindows Sandbox内で実行してください。");
        }

        var window = new MainWindow(commandLine: true) { ShowActivated = false };
        var access = new MainWindow.PlaybackVerificationAccess(window);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-position-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        List<string> checks = [];
        List<object> observations = [];
        Task<int>? running = null;
        try
        {
            window.Show();
            foreach (WindowState state in new[]
                { WindowState.Normal, WindowState.Minimized, WindowState.Maximized })
            {
                foreach (bool visible in new[] { false, true })
                {
                    string gate = Path.Combine(directory, $"{state}-{visible}.go");
                    string source = $$"""
                        PRINT "waiting"
                        WHILE !FOPEN("{{gate}}",F_EXISTS)
                            SLEEP(0.02)
                        WEND
                        OPTION POSITION=200.75,500.75
                        PRINT "positioned"
                        WHILE !FOPEN("{{gate}}.show",F_EXISTS)
                            SLEEP(0.02)
                        WEND
                        LOGPRINT(TRUE,210,260,420,240)
                        PRINT "shown"
                        WHILE !FOPEN("{{gate}}.hide",F_EXISTS)
                            SLEEP(0.02)
                        WEND
                        LOGPRINT(FALSE)
                        PRINT "hidden-again"
                        WHILE !FOPEN("{{gate}}.repeat",F_EXISTS)
                            SLEEP(0.02)
                        WEND
                        LOGPRINT(TRUE)
                        PRINT "shown-again"
                        SLEEP(30)
                        """;
                    running = window.RunCommandLineAsync(source, new ScriptRunOptions([]));
                    await WaitPositionAsync(() => access.Output.Contains("waiting"));
                    LogWindow log = access.GetOrCreateLogWindow();
                    log.WindowState = WindowState.Normal;
                    log.Apply(new ScriptLogView(true, 100, 120, 400, 220));
                    log.WindowState = state;
                    if (!visible)
                    {
                        log.Hide();
                    }

                    access.SetLogView(new ScriptLogView(visible));
                    StopWindow stop = access.StopWindow!;
                    stop.Hide();
                    await Task.Delay(100);
                    nint foreground = PositionGetForegroundWindow();
                    nint stopHandle = new WindowInteropHelper(stop).EnsureHandle();
                    nint logHandle = new WindowInteropHelper(log).EnsureHandle();
                    await File.WriteAllTextAsync(gate, "go");
                    await WaitPositionAsync(() => access.Output.Contains("positioned"));
                    Require(!stop.IsVisible, "POSITIONが非表示の停止画面を表示しました。");
                    Require(log.IsVisible == visible && log.WindowState == state,
                        "POSITIONがログ画面の表示状態を変更しました。");
                    Require(PositionGetForegroundWindow() == foreground,
                        "POSITIONが入力先を変更しました。");
                    (int x, int y, int width, int height) = WindowsScriptWindowLayout.ReadBounds(stopHandle);
                    Require(x == 200 && y == 500, "停止画面の指定座標が反映されません。");
                    observations.Add(new
                    {
                        phase = "before-restore",
                        state = state.ToString(),
                        visible,
                        native_state = PositionState(log, logHandle)
                    });

                    bool reopensMaximized = !visible && state == WindowState.Maximized;
                    if (reopensMaximized)
                    {
                        await File.WriteAllTextAsync(gate + ".show", "go");
                        await WaitPositionAsync(() => access.Output.Contains("shown"));
                        Require(log.IsVisible && log.WindowState == WindowState.Maximized
                            && PositionIsZoomed(logHandle),
                            "LOGPRINT(TRUE)が最大化したログ画面を再表示できません。");
                        Require(PositionGetForegroundWindow() == foreground,
                            "LOGPRINT(TRUE)が入力先を変更しました。");
                        await File.WriteAllTextAsync(gate + ".hide", "go");
                        await WaitPositionAsync(() => access.Output.Contains("hidden-again"));
                        Require(!log.IsVisible && log.WindowState == WindowState.Maximized,
                            "LOGPRINT(FALSE)が最大化を維持して非表示にできません。");
                        await File.WriteAllTextAsync(gate + ".repeat", "go");
                        await WaitPositionAsync(() => access.Output.Contains("shown-again"));
                        Require(log.IsVisible && log.WindowState == WindowState.Maximized
                            && PositionIsZoomed(logHandle)
                            && PositionGetForegroundWindow() == foreground,
                            "2回目の再表示で最大化状態または入力先が変わりました。");
                        checks.Add("logprint-show-maximized-without-activation");
                    }

                    // POSITIONの状態保持を確認した後、明示的に表示して通常へ戻します。
                    if (state == WindowState.Maximized)
                    {
                        // 非表示中のWPF状態変更だけでは、OSの最大化が残る場合があります。
                        // 配置要求の状態保持を確認した後、所有する画面を明示的に復元します。
                        const int showNoActivate = 4;
                        PositionShowWindow(logHandle, showNoActivate);
                        await WaitPositionAsync(() =>
                            log.WindowState == WindowState.Normal
                            && !PositionIsIconic(logHandle)
                            && !PositionIsZoomed(logHandle),
                            () => PositionState(log, logHandle));
                    }

                    log.Apply(new ScriptLogView(true));
                    log.WindowState = WindowState.Normal;
                    (int expectedX, int expectedY, int expectedWidth, int expectedHeight) =
                        reopensMaximized ? (210, 260, 420, 240) : (200, 280, 400, 220);
                    await WaitPositionAsync(() =>
                    {
                        (int lx, int ly, int lw, int lh) = WindowsScriptWindowLayout.ReadBounds(logHandle);
                        return log.WindowState == WindowState.Normal
                            && !PositionIsIconic(logHandle)
                            && !PositionIsZoomed(logHandle)
                            && lx == expectedX && ly == expectedY
                            && lw == expectedWidth && lh == expectedHeight;
                    }, () => PositionState(log, logHandle));
                    Require(log.IsVisible, "明示的な表示後にログ画面が表示されません。");
                    observations.Add(new
                    {
                        state = state.ToString(),
                        visible,
                        stop_x = x,
                        stop_y = y,
                        stop_width = width,
                        stop_height = height,
                        log_x = expectedX,
                        log_y = expectedY,
                        log_width = expectedWidth,
                        log_height = expectedHeight
                    });
                    checks.Add($"position-{state}-{visible}");
                    access.RequestStop();
                    Require(await running.WaitAsync(TimeSpan.FromSeconds(10)) == 130,
                        "配置後の実行を停止できません。");
                    running = null;
                }
            }

            Require(await window.RunCommandLineAsync("OPTION POSITION=0,0",
                new ScriptRunOptions([])) == 0, "取消後の再実行に失敗しました。");
            checks.Add("position-rerun-after-cancellation");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "PASS",
                checks,
                observations,
                window_shown = true,
                input_injection = false
            }));
            return 0;
        }
        catch (Exception failure)
        {
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "FAIL",
                checks,
                observations,
                error = failure.ToString()
            }));
            return 1;
        }
        finally
        {
            if (running is not null)
            {
                access.RequestStop();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }

            window.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitPositionAsync(Func<bool> condition,
        Func<string>? detail = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("画面配置の検証条件が成立しませんでした。"
                    + detail?.Invoke());
            }

            await Task.Delay(20);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint PositionGetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PositionShowWindow(nint window, int command);

    private static string PositionState(LogWindow window, nint handle)
    {
        bool read = PositionGetWindowRect(handle, out PositionRect rect);
        return $"WPF={window.WindowState}; visible={window.IsVisible}; "
            + $"iconic={PositionIsIconic(handle)}; zoomed={PositionIsZoomed(handle)}; "
            + $"normal-bounds={WindowsScriptWindowLayout.ReadBounds(handle)}; "
            + $"actual-read={read}; actual={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PositionRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PositionGetWindowRect(nint window, out PositionRect rect);

    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PositionIsIconic(nint window);

    [DllImport("user32.dll", EntryPoint = "IsZoomed")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PositionIsZoomed(nint window);
}
