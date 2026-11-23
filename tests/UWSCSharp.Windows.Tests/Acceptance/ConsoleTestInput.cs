using Xunit;

namespace UWSCSharp.Windows.Tests;

internal static class ConsoleTestInput
{
    internal static void ClickCloseButton(nint console)
    {
        Assert.False(Native.IsIconic(console));
        _ = Native.SetForegroundWindow(console);
        Assert.Equal(console, Native.GetForegroundWindow());
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect(console, out Native.Rect bounds));
        Assert.True(Native.GetPhysicalCursorPos(out Native.Point previous));
        try
        {
            for (int y = bounds.Top + 8; y < bounds.Top + 40; y += 8)
            {
                for (int x = bounds.Right - 12; x > bounds.Right - 64; x -= 8)
                {
                    if (Native.WindowFromPoint(new Native.Point(x, y)) != console
                        || Native.SendMessageTimeoutW(console, 0x84, 0,
                            (y << 16) | (x & 0xffff), 2, 500, out nuint hit) == 0
                        || hit != 20)
                    {
                        continue;
                    }

                    Assert.True(Native.SetCursorPos(x, y));
                    Assert.Equal(console, Native.GetForegroundWindow());
                    foreach (uint flags in new uint[] { 2, 4 })
                    {
                        NativeInputSender.Send(new Native.Input
                        {
                            Data = new Native.InputUnion
                            {
                                Mouse = new Native.MouseInput { Flags = flags }
                            }
                        });
                    }

                    return;
                }
            }

            Assert.Fail("所有コンソールの閉じるボタンを特定できませんでした。");
        }
        finally
        {
            Native.SetCursorPos(previous.X, previous.Y);
        }
    }

    internal static void SendControlC(nint console)
    {
        _ = Native.SetForegroundWindow(console);
        Assert.Equal(console, Native.GetForegroundWindow());

        SendKey(0x11, 0);
        try
        {
            SendKey(0x43, 0);
            SendKey(0x43, 2);
        }
        finally
        {
            SendKey(0x11, 2);
        }

        static void SendKey(ushort key, uint flags) =>
            NativeInputSender.Send(new Native.Input
            {
                Type = 1,
                Data = new Native.InputUnion
                {
                    Keyboard = new Native.KeyboardInput { Vk = key, Flags = flags }
                }
            });
    }
}
