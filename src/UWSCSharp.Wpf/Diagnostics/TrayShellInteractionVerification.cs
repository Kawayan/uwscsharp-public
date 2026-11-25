using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using UWSCSharp.Windows;
using Forms = System.Windows.Forms;

namespace UWSCSharp.Wpf;

internal static class TrayShellInteractionVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
        if (!SandboxEnvironment.IsCurrent)
        {
            throw new InvalidOperationException("通知領域への入力検証はSandbox内だけで実行します。");
        }

        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        var launched = new List<LauncherCommand>();
        var errors = new List<Exception>();
        var main = new MainWindow(false, confirmDiscard: () => true,
            reportError: errors.Add,
            launcherSettingsPath: Path.Combine(directory, "launcher.json"),
            launchCommand: launched.Add);
        var trayAccess = new MainWindow.TrayVerificationAccess(main);
        var launcher = new MainWindow.LauncherVerificationAccess(main);
        var outside = new Window
        {
            Title = "ランチの外側クリック検証",
            Width = 200,
            Height = 120,
            Left = 20,
            Top = 20
        };
        VerificationAssert.Require(TrayShellInput.GetPhysicalCursorPos(out TrayShellInput.PhysicalPoint saved),
            "検証前のポインター位置");
        try
        {
            outside.Show();
            main.Show();
            launcher.SetSource("// 原稿を保持");
            launcher.SetOutput("出力を保持");
            string status = launcher.Status;
            var command = new LauncherCommand("ゲスト内の起動確認", "target.uws",
                LauncherCommandKind.Script, [""], AppContext.BaseDirectory);
            launcher.SaveSettings(new LauncherSettings([command]));
            trayAccess.Hide();
            TrayIcon tray = trayAccess.Icon!;
            void Trace(string message) => File.AppendAllText(receipt + ".events",
                DateTimeOffset.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            tray.RecordShellInputEvents(Trace);
            await Task.Delay(250);
            Point icon = await tray.PrepareShellIconInputAsync(Trace);
            await TrayShellInput.ClickAsync(icon);
            await WaitFor(() => launcher.Menu?.IsOpen == true);
            VerificationAssert.Require(!main.IsVisible && tray.Visible, "本体を隠したまま実アイコンから開く");
            checks.Add("guest-mouse-opens-launcher-from-explorer-icon");
            Trace(checks[^1]);

            Point other = outside.PointToScreen(new Point(50, 50));
            await TrayShellInput.ClickAsync(other);
            await WaitFor(() => launcher.Menu?.IsOpen == false);
            VerificationAssert.Require(!main.IsVisible, "外側クリックでは本体を復帰させない");
            checks.Add("outside-click-dismisses-popup-without-restoring-main");
            Trace(checks[^1]);

            await Task.Delay(Forms.SystemInformation.DoubleClickTime + 150);
            await TrayShellInput.ClickAsync(await tray.PrepareShellIconInputAsync(Trace));
            await WaitFor(() => launcher.Menu?.IsOpen == true);
            var item = (MenuItem)launcher.Menu!.Items[0];
            item.PreviewMouseLeftButtonDown += (_, _) => Trace("menu-item-mouse-down");
            item.PreviewMouseLeftButtonUp += (_, _) => Trace("menu-item-mouse-up");
            item.Click += (_, _) => Trace("menu-item-click");
            Point? itemCenter = null;
            string? targetObservation = null;
            await WaitFor(() =>
            {
                itemCenter = ReadMenuItemTarget(item, out string observation);
                if (observation != targetObservation)
                {
                    Trace(observation);
                    targetObservation = observation;
                }
                return itemCenter.HasValue;
            });
            await TrayShellInput.ClickAsync(itemCenter!.Value);
            try
            {
                await WaitFor(() => launched.Count == 1 && launcher.Menu?.IsOpen == false);
            }
            catch (OperationCanceledException)
            {
                Trace($"menu-selection-not-confirmed: launched={launched.Count},"
                    + $" open={launcher.Menu?.IsOpen}, errors={errors.Count}");
                throw;
            }
            VerificationAssert.Require(ReferenceEquals(command, launched[0])
                && launcher.Source == "// 原稿を保持" && launcher.IsModified
                && launcher.Output == "出力を保持" && launcher.Status == status,
                "実メニューから同じ起動処理を使い原稿と出力を保持");
            checks.Add("guest-mouse-selects-command-and-preserves-editor-state");
            Trace(checks[^1]);

            await Task.Delay(Forms.SystemInformation.DoubleClickTime + 150);
            icon = await tray.PrepareShellIconInputAsync(Trace);
            await TrayShellInput.ClickAsync(icon);
            await Task.Delay(Math.Clamp(Forms.SystemInformation.DoubleClickTime / 5, 5, 50));
            await TrayShellInput.ClickAsync(icon);
            await WaitFor(() => main.IsVisible && !tray.Visible);
            await Task.Delay(Forms.SystemInformation.DoubleClickTime * 2 + 150);
            VerificationAssert.Require(launcher.Menu?.IsOpen == false && errors.Count == 0,
                "実ダブルクリックでは通常画面の復帰だけを行う");
            checks.Add("guest-mouse-double-click-restores-without-delayed-popup");
            Trace(checks[^1]);
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                checks,
                guest_mouse_input = true,
                shell_callback_injection = false,
                double_click_milliseconds = Forms.SystemInformation.DoubleClickTime
            }));
            return 0;
        }
        finally
        {
            ScriptDropOleVerification.Button(down: false);
            main.Close();
            outside.Close();
            ScriptDropOleVerification.Move(new Point(saved.X, saved.Y));
        }

        static async Task WaitFor(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition())
            {
                await Task.Delay(20, deadline.Token);
            }
        }
    }

    private static Point? ReadMenuItemTarget(MenuItem item, out string observation)
    {
        observation = $"menu-item-layout: visible={item.IsVisible}, enabled={item.IsEnabled},"
            + $" arranged={item.IsArrangeValid}, size={item.ActualWidth},{item.ActualHeight}";
        if (!item.IsVisible || !item.IsEnabled || !item.IsArrangeValid
            || item.ActualWidth < 2 || item.ActualHeight < 2
            || PresentationSource.FromVisual(item) is not HwndSource popup)
        {
            return null;
        }

        // メニュー項目のレイアウトと実際に前面にある入力先を照合します。
        var center = new Point(item.ActualWidth / 2, item.ActualHeight / 2);
        Point screen = item.PointToScreen(center);
        using var dpi = new PhysicalDpiScope();
        nint hit = TrayShellInput.WindowFromPoint(new TrayShellInput.PhysicalPoint
        {
            X = checked((int)screen.X),
            Y = checked((int)screen.Y)
        });
        bool itemHit = item.InputHitTest(center) is not null;
        observation = $"menu-item-target: point={screen.X},{screen.Y}, hwnd={hit},"
            + $" popup={popup.Handle}, item_hit={itemHit}";
        if (hit != popup.Handle || !itemHit)
        {
            return null;
        }

        return screen;
    }
}

internal sealed partial class TrayIcon
{
    internal async Task<Point> PrepareShellIconInputAsync(Action<string> trace)
    {
        Point point = GetShellIconPhysicalCenter();
        PointerSample sample = await Task.Run(() => ReadShellSample(point, point));
        trace($"shell-target={JsonSerializer.Serialize(sample.AtIcon)},point={point.X},{point.Y}");
        if (MatchesVisibleIcon(sample))
        {
            TraceVisibleIcon();
            return point;
        }

        VerificationAssert.Require(sample.AtIcon.IsChevron,
            "観測済みの非表示アイコン表示ボタンを識別する");
        await TrayShellInput.ClickAsync(point);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                await Task.Delay(50, deadline.Token);
                point = GetShellIconPhysicalCenter();
                sample = await Task.Run(() => ReadShellSample(point, point));
            }
            while (!MatchesVisibleIcon(sample));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            trace($"icon-not-confirmed={JsonSerializer.Serialize(sample)}");
            throw;
        }

        TraceVisibleIcon();
        return point;

        void TraceVisibleIcon() =>
            trace($"visible-icon={JsonSerializer.Serialize(sample.NamedMatches[0])},"
                + $"hit={JsonSerializer.Serialize(sample.AtIcon)},point={point.X},{point.Y}");
    }

    private static bool MatchesVisibleIcon(PointerSample sample) =>
        !sample.AtIcon.IsChevron && sample.NamedCount == 1
        && sample.NamedMatches is
            [{ Offscreen: false, Id: "NotifyItemIcon", ClassName: "SystemTray.NormalButton" } icon]
        && icon.Contains(sample.IconX, sample.IconY);

    internal void RecordShellInputEvents(Action<string> trace)
    {
        notification.MouseDown += (_, e) => trace($"down {e.Button} {e.Clicks}");
        notification.MouseUp += (_, e) => trace($"up {e.Button} {e.Clicks}");
        notification.MouseClick += (_, e) => trace($"click {e.Button} {e.Clicks}");
        notification.MouseDoubleClick += (_, e) => trace($"double {e.Button} {e.Clicks}");
    }

    internal Point GetShellIconPhysicalCenter()
    {
        (nint handle, uint id) = VerificationIdentity();
        var identifier = new IconIdentifier
        {
            Size = (uint)Marshal.SizeOf<IconIdentifier>(),
            Window = handle,
            Id = id
        };
        int result = Shell_NotifyIconGetRect(ref identifier, out IconRectangle rectangle);
        if (result != 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        VerificationAssert.Require(rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top,
            $"通知アイコンに実際の配置領域がある（{rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}）");
        var point = new TrayShellInput.PhysicalPoint
        {
            X = rectangle.Left + (rectangle.Right - rectangle.Left) / 2,
            Y = rectangle.Top + (rectangle.Bottom - rectangle.Top) / 2
        };
        nint hit = TrayShellInput.WindowFromPoint(point);
        TrayShellInput.GetWindowThreadProcessId(hit, out uint processId);
        VerificationAssert.Require(processId != 0, $"アイコン中心の入力先（{point.X},{point.Y}）");
        using var process = Process.GetProcessById(checked((int)processId));
        VerificationAssert.Require(process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase),
            $"通知アイコンの位置をExplorerが占める（実際: {process.ProcessName}）");
        return new Point(point.X, point.Y);
    }
}

internal static class TrayShellInput
{
    internal static async Task ClickAsync(Point physicalPoint)
    {
        ScriptDropOleVerification.Move(physicalPoint);
        ScriptDropOleVerification.Button(down: true);
        try
        {
            await Task.Delay(20);
        }
        finally
        {
            ScriptDropOleVerification.Button(down: false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PhysicalPoint
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicalCursorPos(out PhysicalPoint point);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(PhysicalPoint point);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
