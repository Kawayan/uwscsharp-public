using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using UWSCSharp.Windows;

namespace UWSCSharp.Wpf;

/// <summary>
/// GUI診断が所有するゲスト画面を、入力先へ準備します。
/// </summary>
internal static class DiagnosticInputTarget
{
    internal static async Task PrepareAsync(Window window)
    {
        await PrepareAsync(new WindowInteropHelper(window).Handle,
            (uint)Environment.ProcessId, window);
    }

    internal static async Task PrepareAsync(
        nint expected, uint process, Window? window = null)
    {
        if (Environment.UserName != "WDAGUtilityAccount")
        {
            throw new InvalidOperationException("入力先の準備はSandbox専用です。");
        }

        Native.GetWindowThreadProcessId(expected, out uint owner);
        if (owner != process || expected == 0)
        {
            throw new InvalidOperationException("入力先が検証プロセスの所有画面ではありません。");
        }

        if (window is not null)
        {
            _ = window.Activate();
        }

        if (Native.GetForegroundWindow() == expected)
        {
            return;
        }

        Native.Point previous = new NativeDisplayPlatform().CursorPosition();
        try
        {
            using (var dpi = new PhysicalDpiScope())
            {
                if (!Native.SetWindowPos(expected, 0, 0, 0, 0, 0, 0x13)
                    || !Native.GetWindowRect(expected, out Native.Rect bounds))
                {
                    throw new InvalidOperationException("所有画面の位置を確認できません。");
                }

                var point = new Native.Point(
                    (bounds.Left + bounds.Right) / 2, bounds.Top + 16);
                if (Native.WindowFromPoint(point) != expected)
                {
                    throw new InvalidOperationException("クリック先が所有画面ではありません。");
                }

                if (!Native.SetCursorPos(point.X, point.Y))
                {
                    throw new InvalidOperationException("ゲスト内の入力位置を設定できません。");
                }
            }

            // APIで前面化できない場合も、確認した所有画面だけを操作します。
            try
            {
                SendActivationMouse(2);
            }
            finally
            {
                SendActivationMouse(4);
            }

            var elapsed = Stopwatch.StartNew();
            while (Native.GetForegroundWindow() != expected
                && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25);
            }

            nint foreground = Native.GetForegroundWindow();
            if (foreground != expected)
            {
                throw new InvalidOperationException(
                    $"入力先を確保できません: expected={expected}, foreground={foreground}");
            }
        }
        finally
        {
            using var dpi = new PhysicalDpiScope();
            _ = Native.SetCursorPos(previous.X, previous.Y);
        }
    }

    private static void SendActivationMouse(uint flags) =>
        NativeInputSender.Send(new Native.Input
        {
            Type = 0,
            Data = new Native.InputUnion
            {
                Mouse = new Native.MouseInput { Flags = flags }
            }
        });
}
