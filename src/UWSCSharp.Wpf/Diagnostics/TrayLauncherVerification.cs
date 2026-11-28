using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using static UWSCSharp.Wpf.VerificationAssert;
using Forms = System.Windows.Forms;

namespace UWSCSharp.Wpf;

internal static class TrayLauncherVerification
{
    internal static async Task<int> RunAsync(string directory, string receipt)
    {
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
        try
        {
            main.Show();
            launcher.SetSource("// 保持する未保存の原稿");
            launcher.SetOutput("保持する出力");
            string status = launcher.Status;
            var command = new LauncherCommand("起動_項目",
                Path.Combine(directory, "target.uws"), LauncherCommandKind.Script,
                ["", "日本語"], AppContext.BaseDirectory);
            launcher.SaveSettings(new LauncherSettings([command]));
            trayAccess.Hide();
            TrayIcon tray = trayAccess.Icon!;
            tray.RequireShellRegistration();
            int interval = Forms.SystemInformation.DoubleClickTime;
            tray.SendMouseMessage(0x0201);
            tray.SendMouseMessage(0x0202);
            Require(launcher.Menu is null, "単クリック直後は表示しない");
            await WaitFor(() => launcher.Menu?.IsOpen == true);
            Require(!main.IsVisible && tray.Visible
                && launcher.Menu!.Placement == PlacementMode.MousePoint,
                "非表示の本体からポインター位置へ表示");
            var launch = (MenuItem)launcher.Menu!.Items[0];
            launch.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            launcher.Menu!.IsOpen = false;
            Require(launched.SequenceEqual([command]) && launcher.Source.Contains("未保存")
                && launcher.IsModified && launcher.Output == "保持する出力"
                && launcher.Status == status, "メニュー経由の起動と原稿保持");
            checks.Add("shell-registration-native-single-click-delayed-menu-and-launch");

            tray.SendMouseMessage(0x0201);
            tray.SendMouseMessage(0x0202);
            tray.SendMouseMessage(0x0203);
            tray.SendMouseMessage(0x0202);
            await Task.Delay(interval * 2 + 150);
            Require(main.IsVisible && !tray.Visible && !launcher.Menu!.IsOpen
                && launched.Count == 1, "ダブルクリックは復帰だけを行う");
            checks.Add("native-double-click-restores-without-delayed-menu");

            trayAccess.Hide();
            tray.SendMouseMessage(0x0201);
            tray.SendMouseMessage(0x0202);
            trayAccess.Restore();
            await Task.Delay(interval * 2 + 150);
            Require(!launcher.Menu!.IsOpen, "非表示時に単クリック待機を取り消す");
            checks.Add("hide-icon-cancels-pending-single-click");

            trayAccess.Hide();
            tray.SendMouseMessage(0x0204);
            tray.SendMouseMessage(0x0205);
            Require(tray.VerificationContextMenuVisible, "右クリックは従来メニュー");
            tray.CloseVerificationContextMenu();
            await Task.Delay(interval * 2 + 150);
            Require(!launcher.Menu!.IsOpen && !main.IsVisible, "右クリックではランチを出さない");
            checks.Add("native-right-click-keeps-context-menu-without-launcher");

            main.IsEnabled = false;
            tray.SendMouseMessage(0x0201);
            tray.SendMouseMessage(0x0202);
            await Task.Delay(interval * 2 + 150);
            Require(!launcher.Menu!.IsOpen, "モーダル中はランチを出さない");
            main.IsEnabled = true;
            checks.Add("disabled-owner-blocks-launcher");

            tray.SendMouseMessage(0x0201);
            tray.SendMouseMessage(0x0202);
            main.Close();
            tray.Dispose();
            await Task.Delay(interval * 2 + 150);
            Require(trayAccess.Icon is null && !launcher.Menu!.IsOpen
                && errors.Count == 0, "終了と重複解放で待機を取り消す");
            checks.Add("close-disposes-icon-and-cancels-pending-action");
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
            {
                result = "Passed",
                checks,
                double_click_milliseconds = interval,
                shell_callback_messages = true,
                physical_mouse_input = false,
                runtime = Environment.Version.ToString()
            }));
            return 0;
        }
        finally
        {
            main.IsEnabled = true;
            main.Close();
        }

        static async Task WaitFor(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition())
            {
                await Task.Delay(20, timeout.Token);
            }
        }
    }
}

internal sealed partial class TrayIcon
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    // .NET 11の実NotifyIconへ、シェルと同じコールバックを送る診断専用の入口です。
    internal void SendMouseMessage(int mouseMessage)
    {
        (nint handle, uint id) = VerificationIdentity();
        SendMessage(handle, 0x0800, id, mouseMessage);
    }

    internal bool VerificationContextMenuVisible => menu.Visible;

    internal void CloseVerificationContextMenu() => menu.Close();

    internal void RequireShellRegistration()
    {
        (nint handle, uint id) = VerificationIdentity();
        var identifier = new IconIdentifier
        {
            Size = (uint)Marshal.SizeOf<IconIdentifier>(),
            Window = handle,
            Id = id
        };
        int result = Shell_NotifyIconGetRect(ref identifier, out _);
        if (result != 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private (nint Handle, uint Id) VerificationIdentity()
    {
        var native = (Forms.NativeWindow)typeof(Forms.NotifyIcon)
            .GetField("_window", PrivateInstance)!.GetValue(notification)!;
        uint id = (uint)typeof(Forms.NotifyIcon)
            .GetField("_id", PrivateInstance)!.GetValue(notification)!;
        return native.Handle != 0 ? (native.Handle, id)
            : throw new InvalidOperationException("通知アイコンの受信先がありません。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out IconRectangle rectangle);
}
