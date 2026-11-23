using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// ゲスト内の所有画面を、実入力が届く状態へ準備します。
/// </summary>
internal static class UiInputDriver
{
    internal static async Task ActivateAsync(nint window, Native.Point? activationPoint = null)
    {
        Native.GetWindowThreadProcessId(window, out uint process);
        Assert.Equal((uint)Environment.ProcessId, process);
        _ = Native.SetForegroundWindow(window);
        if (Native.GetForegroundWindow() == window)
        {
            return;
        }

        Native.Point previous = new NativeDisplayPlatform().CursorPosition();
        try
        {
            using (var dpi = new PhysicalDpiScope())
            {
                Assert.True(SetWindowPos(window, 0, 0, 0, 0, 0, 0x13));
                Assert.True(Native.GetWindowRect(window, out Native.Rect rect));
                Native.Point point = activationPoint ?? new Native.Point((rect.Left + rect.Right) / 2, rect.Top + 16);
                Assert.Equal(window, Native.WindowFromPoint(point));
                Assert.True(Native.SetCursorPos(point.X, point.Y));
            }

            foreach (uint flags in new uint[] { 2, 4 })
            {
                NativeInputSender.Send(new Native.Input
                {
                    Type = 0,
                    Data = new Native.InputUnion { Mouse = new Native.MouseInput { Flags = flags } }
                });
            }

            var timer = Stopwatch.StartNew();
            while (Native.GetForegroundWindow() != window && timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(25);
            }

            Assert.True(Native.GetForegroundWindow() == window,
                $"所有画面の前面条件が不成立です。expected={window:X}, actual={Native.GetForegroundWindow():X}");
        }
        finally
        {
            using var dpi = new PhysicalDpiScope();
            Native.SetCursorPos(previous.X, previous.Y);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after,
        int x, int y, int width, int height, uint flags);
}
